using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Provenance of an agent result bound to a validated durable terminal response.
/// An acceptance receipt is not evidence that the parent sampled this message.</summary>
/// <param name="TurnId">Completed child turn.</param>
/// <param name="RootTurnId">Original reasoning root turn.</param>
/// <param name="ResponseId">Validated final provider response.</param>
/// <param name="OutputSha256">Digest of the validated final response items.</param>
public sealed record CodexAgentCompletedResult(Guid TurnId, Guid RootTurnId, string ResponseId, string OutputSha256)
{
    internal bool IsValid => TurnId != Guid.Empty && RootTurnId != Guid.Empty &&
        ResponseId is { Length: > 0 and <= 512 } && ResponseId.All(c => c is >= '!' and <= '~') &&
        OutputSha256 is { Length: 64 } && OutputSha256.All(Uri.IsHexDigit);

    /// <summary>Stable result delivery identity. Repeated inspection of one completed child turn
    /// yields the same ID across process restarts. Different turns never intentionally reuse it.</summary>
    public static Guid DeliveryId(Guid childThreadId, Guid turnId)
    {
        if (childThreadId == Guid.Empty || turnId == Guid.Empty)
            throw new ArgumentException("Child and completed turn identities must be nonempty.");
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(
            "codex:agent-result:v1:" + childThreadId.ToString("N") + turnId.ToString("N")));
        // RFC 9562 UUID version 8: the domain-separated digest supplies the remaining bits.
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>Derive the immutable result from the original completed journal. Do not use
    /// commentary from earlier requests, a caller-supplied text, or a speculative tool result.
    /// Unsupported legacy/direct-tool completions are refused rather than fabricating an answer.</summary>
    public static CodexAgentMail FromCompletedTurn(Guid childThreadId, CodexStoredTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Status != "completed" || turn.CompletedAt is null)
            throw new InvalidOperationException("Agent result requires an observed completed turn.");
        var receipt = turn.Dispatches.LastOrDefault(item => item.Kind == "inference");
        if (receipt is not { State: "succeeded", InferenceCompletion: { } completion } ||
            !new CodexAgentCompletedResult(turn.Id, turn.RootTurnId, completion.ResponseId, completion.OutputSha256).IsValid ||
            completion.OutputStart < 0 || completion.OutputCount < 0 ||
            completion.OutputStart > turn.Output.Length - completion.OutputCount)
            throw new InvalidOperationException("Agent result requires an exact validated final response.");
        // Existing recovery logic verifies the original prefix, response digest and persisted output.
        if (CodexInferenceRecovery.RecoverOutput(turn).Length != turn.Output.Length)
            throw new InvalidDataException("Completed agent output is not fully durable.");
        var output = turn.Output.Skip(completion.OutputStart).Take(completion.OutputCount).ToArray();
        if (output.Any(item => CodexRealtimeProtocol.Text(item, "type") is "function_call" or "custom_tool_call"))
            throw new InvalidOperationException("Tool-only terminal result requires separate durable result evidence.");
        var text = string.Concat(output.Where(item => CodexRealtimeProtocol.Text(item, "type") == "message" &&
                CodexRealtimeProtocol.Text(item, "role") == "assistant")
            .SelectMany(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray().Where(part => CodexRealtimeProtocol.Text(part, "type") == "output_text")
                    .Select(part => CodexRealtimeProtocol.Text(part, "text") ?? string.Empty)
                : Enumerable.Empty<string>()));
        return new CodexAgentMail(DeliveryId(childThreadId, turn.Id), childThreadId, CodexAgentMailMode.Result, text)
        {
            CompletedResult = new(turn.Id, turn.RootTurnId, completion.ResponseId, completion.OutputSha256),
        };
    }

    /// <summary>Compare a proposed bound result against the original completed child turn.
    /// A host adapter uses this check inside its own atomic recipient mailbox transaction.</summary>
    public static bool Matches(CodexAgentMail mail, CodexStoredTurn turn)
        => mail.CompletedResult is not null && FromCompletedTurn(mail.SenderThreadId, turn) == mail;
}
