using System.Text;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Local plan state tool. It cannot authorize filesystem, process or device effects.</summary>
public static class CodexTurnPlan
{
    public const string ToolName = "update_plan";
    public static JsonElement Tool { get; } = Parse("""
        {"type":"function","name":"update_plan","description":"Update the current task plan. This records progress only and grants no action permissions.",
        "parameters":{"type":"object","properties":{"explanation":{"type":"string"},"plan":{"type":"array","items":{"type":"object","properties":{"step":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","completed"]}},"required":["step","status"],"additionalProperties":false}}},"required":["plan"],"additionalProperties":false}}
        """);

    public static async Task<CodexToolOutput> ExecuteAsync(string arguments, Func<JsonElement, Task> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(arguments) > 65_536) throw new ArgumentException("Plan exceeds its bound.");
        var value = Parse(arguments);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("explanation" or "plan"))
            || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()
            || !value.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Array || plan.GetArrayLength() > 128)
            throw new ArgumentException("A bounded plan is required.");
        if (value.TryGetProperty("explanation", out var explanation) && explanation.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new ArgumentException("Explanation must be text or null.");
        var inProgress = 0;
        foreach (var step in plan.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || step.EnumerateObject().Count() != 2
                || step.EnumerateObject().Any(p => p.Name is not ("step" or "status"))
                || !step.TryGetProperty("step", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString())
                || !step.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                || status.GetString() is not ("pending" or "in_progress" or "completed"))
                throw new ArgumentException("Invalid plan step.");
            if (status.GetString() == "in_progress" && ++inProgress > 1) throw new ArgumentException("Only one step may be in progress.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await publish(value).ConfigureAwait(false);
        return new("Plan updated");
    }

    private static JsonElement Parse(string text)
    { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
}
