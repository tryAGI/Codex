using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Validated normal inference output, bound to its original durable output boundary.
/// The provider terminal is stored in the dispatch receipt. Only sparse terminals need
/// an additional copy of their reconciled streamed output.</summary>
/// <param name="ResponseId">Correlated provider response identity.</param>
/// <param name="OutputStart">Number of original output items preceding the response.</param>
/// <param name="PrefixSha256">Digest of that immutable output prefix.</param>
/// <param name="OutputCount">Number of validated response output items.</param>
/// <param name="OutputSha256">Digest of the validated response output.</param>
/// <param name="SparseOutput">Reconciled output when the provider terminal omitted it.</param>
public sealed record CodexStoredInferenceCompletion(
    [property: JsonRequired] string ResponseId,
    [property: JsonRequired] int OutputStart,
    [property: JsonRequired] string PrefixSha256,
    [property: JsonRequired] int OutputCount,
    [property: JsonRequired] string OutputSha256,
    JsonElement[]? SparseOutput = null)
{
    /// <summary>Original reservation provenance, absent in legacy validated receipts.</summary>
    public CodexStoredInferenceAccounting? Accounting { get; init; }
}

internal static class CodexInferenceRecovery
{
    private const int MaximumOutput = 32768;
    private const int MaximumResponseItems = 4096;

    internal static CodexStoredInferenceCompletion Capture(CodexStoredTurn turn, string responseId,
        JsonElement response, IReadOnlyList<JsonElement> output)
    {
        if (CodexRealtimeProtocol.Text(response, "id") != responseId ||
            output.Count > MaximumResponseItems || turn.Output.Length > MaximumOutput - output.Count ||
            output.Any(item => item.ValueKind != JsonValueKind.Object))
            throw new CodexProtocolException("Validated inference output exceeds its native recovery boundary.");
        var raw = RawOutput(response);
        if (raw.Length > 0 && !CodexThreadStore.SameHistory(raw, output))
            throw new CodexProtocolException("Validated inference output differs from its terminal receipt.");
        return new(responseId, turn.Output.Length, Hash(turn.Output), output.Count, Hash(output),
            raw.Length == 0 && output.Count > 0 ? output.Select(item => item.Clone()).ToArray() : null);
    }

    private static JsonElement[] RawOutput(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid inference recovery response.");
        if (!response.TryGetProperty("output", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumResponseItems)
            throw new InvalidDataException("Invalid inference recovery output.");
        return value.EnumerateArray().ToArray();
    }

    private static string Hash(IEnumerable<JsonElement> items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items) item.WriteTo(writer);
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    // Cheap structural validation on deserialization. Exact prefix/output digests
    // are checked only when recovering, avoiding repeated hashing of old prompts.
    internal static void Validate(CodexStoredTurn turn)
    {
        foreach (var receipt in turn.Dispatches ?? [])
        {
            if (receipt?.InferenceCompletion is not { } completion) continue;
            if (receipt.Kind != "inference" || receipt.State != "succeeded" ||
                receipt.Result is not { ValueKind: JsonValueKind.Object } response ||
                string.IsNullOrWhiteSpace(completion.ResponseId) || completion.ResponseId.Length > 512 ||
                CodexRealtimeProtocol.Text(response, "id") != completion.ResponseId ||
                response.TryGetProperty("status", out var status) &&
                    (status.ValueKind != JsonValueKind.String || status.GetString() != "completed") ||
                turn.Output is null || completion.OutputStart < 0 || completion.OutputStart > turn.Output.Length ||
                completion.OutputCount < 0 || completion.OutputCount > MaximumResponseItems ||
                completion.OutputStart > MaximumOutput - completion.OutputCount ||
                !ValidHash(completion.PrefixSha256) || !ValidHash(completion.OutputSha256) ||
                completion.Accounting is { } accounting && !CodexInferenceAccounting.Valid(accounting, turn.Id))
                throw new InvalidDataException("Invalid validated inference recovery boundary.");
            var rawCount = 0;
            if (response.TryGetProperty("output", out var raw))
            {
                if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > MaximumResponseItems)
                    throw new InvalidDataException("Invalid validated inference terminal output.");
                rawCount = raw.GetArrayLength();
            }
            if (completion.SparseOutput is { } sparse
                ? rawCount != 0 || sparse.Length == 0 || sparse.Length != completion.OutputCount ||
                    sparse.Any(item => item.ValueKind != JsonValueKind.Object)
                : rawCount != completion.OutputCount)
                throw new InvalidDataException("Invalid reconciled inference output.");
        }
    }

    internal static JsonElement[] RecoverOutput(CodexStoredTurn turn)
    {
        var index = Array.FindLastIndex(turn.Dispatches, receipt => receipt.InferenceCompletion is not null);
        if (index < 0) return turn.Output;
        Validate(turn);
        var receipt = turn.Dispatches[index];
        var completion = receipt.InferenceCompletion!;
        var output = completion.SparseOutput ?? RawOutput(receipt.Result!.Value);
        if (Hash(turn.Output.Take(completion.OutputStart)) != completion.PrefixSha256 ||
            Hash(output) != completion.OutputSha256)
            throw new InvalidDataException("Validated inference recovery digest mismatch.");
        var existing = Math.Min(output.Length, turn.Output.Length - completion.OutputStart);
        for (var position = 0; position < existing; position++)
            if (!JsonElement.DeepEquals(turn.Output[completion.OutputStart + position], output[position]))
                throw new InvalidDataException("Validated inference recovery conflicts with the original journal.");
        if (existing == output.Length) return turn.Output;
        // A later dispatch implies this response was already consumed. Never fill
        // a gap behind subsequent execution or infer a tool receipt from output.
        if (index != turn.Dispatches.Length - 1)
            throw new InvalidDataException("Cannot restore missing inference output behind a later dispatch.");
        return [.. turn.Output, .. output.Skip(existing).Select(item => item.Clone())];
    }

    internal static void WriteReceipt(Utf8JsonWriter writer, CodexStoredDispatch receipt)
    {
        if (receipt.InferenceCompletion is not { } completion) return;
        writer.WriteStartObject("inference_completion");
        writer.WriteString("response", completion.ResponseId);
        writer.WriteNumber("output_start", completion.OutputStart);
        writer.WriteString("prefix_sha256", completion.PrefixSha256);
        writer.WriteNumber("output_count", completion.OutputCount);
        writer.WriteString("output_sha256", completion.OutputSha256);
        // The digest covers both the terminal and any separately retained sparse output.
        if (completion.Accounting is { } accounting)
            CodexInferenceAccounting.WriteBinding(writer, accounting);
        writer.WriteEndObject();
    }
}
