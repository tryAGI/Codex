using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed record CodexCustomToolProbeResult(string Model, string ReasoningEffort,
    bool InputMatched, bool ResultMatched, int Requests, int ToolCalls, int EffectsExecuted = 0);

/// <summary>Explicit two-request synthetic custom-tool canary. It never reads host
/// context, edits files, launches commands or sends playback. The caller owns cost admission.</summary>
public static class CodexCustomToolProbe
{
    public static async Task<CodexCustomToolProbeResult> RunAsync(CodexClient client, string model, string effort,
        CancellationToken cancellationToken = default, Action<CodexResponseDiagnostic>? observeDiagnostics = null)
    {
        const string name = "native_protocol_fixture";
        var marker = Guid.NewGuid().ToString("N");
        // The reply is intentionally absent from the request: a successful second
        // answer must consume the exact custom_tool_call_output, not repeat input.
        var reply = Guid.NewGuid().ToString("N");
        var calls = 0; var matched = true;
        using var definition = JsonDocument.Parse(new JsonObject
        {
            ["type"] = "custom", ["name"] = name,
            ["description"] = "Synthetic verification fixture. Input is plain text; do not wrap in JSON.",
            ["format"] = new JsonObject { ["type"] = "grammar", ["syntax"] = "lark", ["definition"] = "start: /[a-f0-9]{32}/" }
        }.ToJsonString());
        using var message = JsonDocument.Parse(new JsonObject
        {
            ["role"] = "user", ["content"] = "Call native_protocol_fixture once with this exact verification text: " + marker
                + ". Then answer only with the text returned by that tool."
        }.ToJsonString());
        var options = new CodexResponseOptions(model,
            "Perform this synthetic protocol verification. Use the advertised custom tool once, then return only its output text. No other actions.")
        { Tools = [definition.RootElement.Clone()], ReasoningEffort = effort };
        var result = await new CodexAgent(client).RunWithToolsAsync(options, [message.RootElement.Clone()], (call, ct) =>
        {
            ct.ThrowIfCancellationRequested(); calls++;
            matched &= call.Kind == CodexToolCallKind.Custom && call.Name == name && call.Input == marker;
            return Task.FromResult(new CodexToolOutput(matched ? reply : "INVALID_VERIFICATION_INPUT"));
        }, maximumRequests: 2, cancellationToken: cancellationToken, observeDiagnostics: observeDiagnostics).ConfigureAwait(false);
        return new(model, effort, calls == 1 && matched, calls == 1 && matched && result.Text.Trim() == reply,
            result.Requests, result.ToolCalls);
    }
}
