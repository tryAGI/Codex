using System.Text.Json;

namespace tryAGI.Codex;

public enum CodexToolCallKind { Function, Custom }

/// <summary>An authorized tool identity and its exact wire input. Function Input is
/// a JSON object encoded as text; Custom Input is freeform text, never shell code.</summary>
public sealed record CodexToolCall(string Id, string Name, string Input, CodexToolCallKind Kind)
{
    public string? Namespace { get; init; }
    public string? Status { get; init; }

    internal CodexFunctionCall Function() => Kind == CodexToolCallKind.Function
        ? new(Id, Name, Input) { Namespace = Namespace }
        : throw new CodexProtocolException("The function-only executor cannot dispatch a custom tool.");
}

internal static class CodexToolCalls
{
    // Rust ToolName considers omitted, empty and "functions" namespaces equivalent.
    private const string DefaultNamespace = "functions";
    internal static string NormalizeNamespace(string? value) => string.IsNullOrEmpty(value) ? DefaultNamespace : value;

    internal static Dictionary<(string Namespace, string Name), CodexToolCallKind> Advertised(
        IReadOnlyList<JsonElement> tools, bool allowCustom)
    {
        var result = new Dictionary<(string, string), CodexToolCallKind>();
        void Add(JsonElement tool, string? toolNamespace)
        {
            var type = CodexRealtimeProtocol.Text(tool, "type");
            var kind = type switch
            {
                "function" => CodexToolCallKind.Function,
                "custom" when allowCustom => CodexToolCallKind.Custom,
                "custom" => throw new ArgumentException("Custom tools require RunWithToolsAsync."),
                _ => throw new ArgumentException("A namespace may contain only function/custom tools.")
            };
            var name = CodexRealtimeProtocol.Text(tool, "name");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Advertised tool has no name.");
            var normalized = NormalizeNamespace(toolNamespace);
            if (!allowCustom && normalized != DefaultNamespace)
                throw new ArgumentException("Namespaced tools require RunWithToolsAsync.");
            if (!result.TryAdd((normalized, name), kind))
                throw new ArgumentException("Ambiguous duplicate advertised tool identity.");
        }
        foreach (var tool in tools)
        {
            if (tool.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool definition must be an object.");
            var type = CodexRealtimeProtocol.Text(tool, "type");
            if (type == "namespace")
            {
                var name = CodexRealtimeProtocol.Text(tool, "name");
                if (string.IsNullOrWhiteSpace(name) || !tool.TryGetProperty("tools", out var children) || children.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("Namespace definition requires a name and tools array.");
                foreach (var child in children.EnumerateArray()) Add(child, name);
            }
            else if (type is "function" or "custom") Add(tool, null);
            // Hosted provider tools are retained on the wire but never authorize a
            // caller-side function/custom dispatch merely by having the same name.
        }
        return result;
    }

    internal static CodexToolCall Validate(JsonElement item,
        Dictionary<(string Namespace, string Name), CodexToolCallKind> advertised, bool allowCustom)
    {
        var kind = CodexRealtimeProtocol.Text(item, "type") == "custom_tool_call" ? CodexToolCallKind.Custom : CodexToolCallKind.Function;
        if (!allowCustom && kind == CodexToolCallKind.Custom)
            throw new CodexProtocolException("The function-only executor cannot dispatch a custom tool.");
        var id = CodexRealtimeProtocol.Text(item, "call_id");
        var name = CodexRealtimeProtocol.Text(item, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new CodexProtocolException("Tool call requires nonempty ID and name.");
        string? toolNamespace = null;
        if (item.TryGetProperty("namespace", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String) throw new CodexProtocolException("Tool namespace must be text or null.");
            toolNamespace = value.GetString();
        }
        if (!advertised.TryGetValue((NormalizeNamespace(toolNamespace), name), out var expected) || kind != expected)
            throw new CodexProtocolException("Model requested an unadvertised tool type/name/namespace.");
        var field = kind == CodexToolCallKind.Custom ? "input" : "arguments";
        var input = CodexRealtimeProtocol.Text(item, field) ?? throw new CodexProtocolException("Tool call has no text input.");
        if (kind == CodexToolCallKind.Function)
        {
            using var parsed = JsonDocument.Parse(input);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Function arguments must be an object.");
        }
        string? callStatus = null;
        if (kind == CodexToolCallKind.Custom && item.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null)
        {
            if (status.ValueKind != JsonValueKind.String) throw new CodexProtocolException("Custom status must be text or null.");
            // Rust's router retains this optional string but does not use it as a
            // completion fence. The agent requires the complete provider response;
            // metadata on an already finalized item may still say in_progress.
            callStatus = status.GetString();
        }
        return new(id, name, input, kind) { Namespace = toolNamespace, Status = callStatus };
    }
}
