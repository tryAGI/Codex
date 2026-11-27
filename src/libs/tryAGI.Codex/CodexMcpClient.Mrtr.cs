using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>One modern MCP input-required round. The state, original parameters and response
/// are opaque, potentially sensitive wire data. Do not log them or use them as permission grants.</summary>
public sealed record CodexMcpInputRound(string Method, int Round, JsonElement OriginalParameters,
    string RequestState, JsonElement InputRequests, JsonElement RawResult)
{
    /// <summary>Redacted diagnostic representation.</summary>
    public override string ToString() => $"MCP {Method} input round {Round}";
}

public sealed partial class CodexMcpClient
{
    /// <summary>Invokes a modern tool with explicit host-provided MRTR elicitation handling.
    /// Each continuation is separately authorized with its original arguments and exact state.
    /// A null host response stops the operation; no approval or response is synthesized.</summary>
    public Task<JsonElement> CallToolWithInputAsync(string name, JsonElement? arguments,
        Func<CodexMcpInputRound, CancellationToken, Task<JsonElement?>>? resolveInput,
        JsonElement? metadata = null, int maximumRounds = 8, CancellationToken cancellationToken = default) =>
        InvokeWithInputAsync("tools/call", "tools", WithMetadata(Named(name, arguments), metadata),
            resolveInput, maximumRounds, cancellationToken);

    /// <summary>Reads a resource using the same explicit modern MRTR continuation contract.</summary>
    public Task<JsonElement> ReadResourceWithInputAsync(string uri,
        Func<CodexMcpInputRound, CancellationToken, Task<JsonElement?>>? resolveInput,
        JsonElement? metadata = null, int maximumRounds = 8, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return InvokeWithInputAsync("resources/read", "resources",
            WithMetadata(new JsonObject { ["uri"] = uri }, metadata), resolveInput, maximumRounds, cancellationToken);
    }

    private async Task<JsonElement> InvokeWithInputAsync(string method, string capability,
        JsonObject original, Func<CodexMcpInputRound, CancellationToken, Task<JsonElement?>>? resolveInput,
        int maximumRounds, CancellationToken cancellationToken)
    {
        if (maximumRounds is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumRounds));
        if (State != CodexMcpState.Ready || ServerInfo is not { ProtocolVersion: ModernProtocolVersion } info)
            throw new InvalidOperationException("MRTR requires an initialized modern MCP connection.");
        if (!info.Capabilities.TryGetProperty(capability, out var advertised) || advertised.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"MCP server did not advertise {capability} capability.");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop);
        var ct = operation.Token;
        var originalSnapshot = ToElement(original);
        var parameters = (JsonObject)original.DeepClone();
        var observedStates = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (State != CodexMcpState.Ready) throw new IOException("MCP connection is no longer ready.");
            // RequestAsync owns wire correlation, timeout and independent host admission.
            var result = await RequestAsync(method, parameters, ct).ConfigureAwait(false);
            RequireUniqueObject(result, "MCP result");
            var kind = CodexRealtimeProtocol.Text(result, "resultType");
            if (kind == "complete") return result;
            if (kind != "input_required")
                throw new CodexProtocolException("Unknown modern MCP result type.");
            if (round == maximumRounds)
                throw new CodexProtocolException("MCP input continuation limit exceeded.");
            if (!result.TryGetProperty("requestState", out var state) || state.ValueKind != JsonValueKind.String ||
                state.GetString() is not { } opaque || !observedStates.Add(opaque))
                throw new CodexProtocolException("Missing or repeated MCP continuation state.");

            parameters = (JsonObject)original.DeepClone();
            parameters["requestState"] = opaque;
            if (!result.TryGetProperty("inputRequests", out var requests) || requests.ValueKind == JsonValueKind.Null)
                continue;
            RequireUniqueObject(requests, "MCP input requests");
            var inputs = requests.EnumerateObject().ToArray();
            if (inputs.Length == 0) continue;
            if (inputs.Length > 32) throw new CodexProtocolException("MCP input request limit exceeded.");
            foreach (var input in inputs) ValidateInputRequest(input);
            if (resolveInput is null) throw new UnauthorizedAccessException("MCP input has no authorized host responder.");

            // User input waiting is outside the per-wire-request timeout, but remains
            // cancellable by the operation or the connection. The host owns UI and proof validation.
            var response = await resolveInput(new(method, round + 1, originalSnapshot, opaque,
                requests.Clone(), result.Clone()), ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (response is not { } values)
                throw new UnauthorizedAccessException("MCP input continuation was not authorized by the host.");
            ValidateInputResponses(inputs, values);
            parameters["inputResponses"] = JsonNode.Parse(values.GetRawText());
        }
    }

    private void ValidateInputRequest(JsonProperty input)
    {
        if (string.IsNullOrEmpty(input.Name)) throw new CodexProtocolException("MCP input request ID is empty.");
        RequireUniqueObject(input.Value, "MCP input request");
        if (CodexRealtimeProtocol.Text(input.Value, "method") != "elicitation/create" ||
            !input.Value.TryGetProperty("params", out var parameters))
            throw new CodexProtocolException("Unsupported MCP input request method.");
        RequireUniqueObject(parameters, "MCP elicitation parameters");
        var mode = CodexRealtimeProtocol.Text(parameters, "mode");
        if (parameters.TryGetProperty("mode", out var explicitMode) &&
            explicitMode.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new CodexProtocolException("Invalid MCP elicitation mode.");
        if (string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(parameters, "message")))
            throw new CodexProtocolException("MCP elicitation message is missing.");
        if ((mode is null or "form") && protocolOptions.EnableFormElicitation &&
            parameters.TryGetProperty("requestedSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
            return;
        if (mode == "url" && protocolOptions.EnableUrlElicitation &&
            !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(parameters, "url")) &&
            !string.IsNullOrWhiteSpace(CodexRealtimeProtocol.Text(parameters, "elicitationId")))
            return;
        // Other input families, including native verification, require separate contracts.
        throw new CodexProtocolException("MCP elicitation mode is not enabled or supported.");
    }

    private static void ValidateInputResponses(JsonProperty[] inputs, JsonElement responses)
    {
        RequireUniqueObject(responses, "MCP input responses");
        var expected = inputs.ToDictionary(input => input.Name, input => input.Value, StringComparer.Ordinal);
        foreach (var response in responses.EnumerateObject())
        {
            if (!expected.Remove(response.Name, out var request))
                throw new CodexProtocolException("MCP response references an unknown input request.");
            RequireUniqueObject(response.Value, "MCP input response");
            var action = CodexRealtimeProtocol.Text(response.Value, "action");
            if (action is not ("accept" or "decline" or "cancel"))
                throw new CodexProtocolException("Invalid MCP input response action.");
            var mode = CodexRealtimeProtocol.Text(request.GetProperty("params"), "mode");
            var content = response.Value.TryGetProperty("content", out var provided) ? provided : default;
            if (action != "accept" && content.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                throw new CodexProtocolException("Declined MCP input must not include content.");
            if (action == "accept" && mode is null or "form" && content.ValueKind != JsonValueKind.Object)
                throw new CodexProtocolException("Accepted MCP form input requires an object.");
            if (action == "accept" && mode == "url" &&
                content.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
                throw new CodexProtocolException("Invalid MCP URL input response.");
        }
        if (expected.Count != 0) throw new CodexProtocolException("Missing MCP input response.");
    }

    private static void RequireUniqueObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new CodexProtocolException($"{description} must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name))
                throw new CodexProtocolException($"{description} contains duplicate members.");
    }

    private static JsonObject WithMetadata(JsonObject parameters, JsonElement? metadata)
    {
        if (metadata is { } raw)
        {
            RequireUniqueObject(raw, "MCP request metadata");
            parameters["_meta"] = JsonNode.Parse(raw.GetRawText());
        }
        return parameters;
    }
}
