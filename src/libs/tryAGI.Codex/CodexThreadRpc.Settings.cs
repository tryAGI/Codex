using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private static string? InputIdentity(JsonElement parameters, string name)
    {
        var value = Text(parameters, name);
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)))
            throw Invalid("Invalid input metadata: " + name);
        return value;
    }

    private static JsonElement[] ToolOutputInput(JsonElement input, JsonElement tool)
    {
        if (input.GetArrayLength() != 0 || tool.ValueKind != JsonValueKind.Object)
            throw Invalid("toolOutput requires an object and empty input.");
        ValidateFields(tool, ["name", "namespace", "output"]);
        var name = InputIdentity(tool, "name") ?? throw Invalid("toolOutput.name is required.");
        var ns = InputIdentity(tool, "namespace");
        if (!tool.TryGetProperty("output", out var body) || body.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
            throw Invalid("toolOutput.output must be text or a content array.");
        if (body.ValueKind == JsonValueKind.Array)
        {
            if (body.GetArrayLength() > 128) throw Invalid("Tool output content exceeds its bound.");
            foreach (var part in body.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) throw Invalid("Invalid tool output content.");
                var type = Text(part, "type");
                switch (type)
                {
                    case "input_text": ValidateFields(part, ["type", "text"]); _ = Text(part, "text") ?? throw Invalid("Missing tool text."); break;
                    case "input_image":
                        ValidateFields(part, ["type", "image_url", "detail"]);
                        var url = Text(part, "image_url") ?? throw Invalid("File IDs require host permission resolution.");
                        if (!(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") && !url.StartsWith("data:image/", StringComparison.Ordinal))
                            throw Invalid("Explicit https or inline image required.");
                        if (Text(part, "detail") is { } detail && detail is not ("auto" or "low" or "high" or "original")) throw Invalid("Invalid image detail.");
                        break;
                    case "input_audio": throw Invalid("Tool audio requires an accounted host media resolver.");
                    case "encrypted_content": ValidateFields(part, ["type", "encrypted_content"]); _ = Text(part, "encrypted_content") ?? throw Invalid("Missing encrypted content."); break;
                    default: throw Invalid("Unknown tool output content type.");
                }
            }
        }
        return [Element(new JsonObject { ["type"] = "function_call_output", ["name"] = name, ["namespace"] = ns,
            ["output"] = JsonNode.Parse(body.GetRawText()) })];
    }

    // Translate the reviewed native consumer keys from the resolved configuration layers;
    // never persist an arbitrary configuration key while pretending it took effect.
    private static void ApplyConfig(JsonObject settings, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("config", out var config) || config.ValueKind == JsonValueKind.Null) return;
        if (config.ValueKind != JsonValueKind.Object) throw Invalid("config must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in config.EnumerateObject())
        {
            if (!seen.Add(field.Name)) throw Invalid("Duplicate config setting.");
            var target = ConfigTarget(field.Name);
            if (field.Value.ValueKind != JsonValueKind.String) throw Invalid("Native config setting must be a string: " + field.Name);
            settings[target] = JsonNode.Parse(field.Value.GetRawText());
        }
        // Typed RPC overrides have higher precedence than this layer. Validate the
        // effective settings in Resolve, after that layer has been applied.
    }
    private static string ConfigTarget(string name) => name switch
    {
        "model" => "model", "model_provider" => "modelProvider", "model_reasoning_effort" => "effort",
        "model_reasoning_summary" => "summary", "model_verbosity" => "verbosity",
        "service_tier" => "serviceTier", "cwd" => "cwd",
        "approval_policy" => "approvalPolicy", "approvals_reviewer" => "approvalsReviewer",
        "sandbox_mode" => "sandbox", "developer_instructions" => "developerInstructions",
        "base_instructions" => "baseInstructions", _ => throw Invalid("Native config resolver does not implement: " + name)
    };
}
