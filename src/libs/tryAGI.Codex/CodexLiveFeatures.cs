using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>A multiple-choice question the provider answers.</summary>
/// <param name="Name">Identifier of the question, echoed in the answer.</param>
/// <param name="Instructions">What to decide.</param>
/// <param name="Choices">Allowed answers.</param>
public sealed record CodexLiveChoiceQuestion(string Name, string Instructions, IReadOnlyList<string> Choices);

/// <summary>Questions answered automatically whenever a session event of one type occurs.</summary>
/// <param name="EventType">The session event type that triggers the questions.</param>
/// <param name="Questions">Questions to answer.</param>
public sealed record CodexLiveDecisionTrigger(string EventType, IReadOnlyList<CodexLiveChoiceQuestion> Questions);

/// <summary>The provider's answer to one choice question.</summary>
/// <param name="Name">Question name.</param>
/// <param name="Choice">Selected choice.</param>
/// <param name="Confidence">Provider confidence, 0–1, if reported.</param>
public sealed record CodexLiveDecisionAnswer(string Name, string Choice, double? Confidence);

/// <summary>Answers produced by a decision trigger.</summary>
/// <param name="SourceEventId">The event that triggered the decision.</param>
/// <param name="SourceEventType">Type of that event.</param>
/// <param name="TriggerId">Provider trigger ID.</param>
/// <param name="Answers">Answers, one per question.</param>
public sealed record CodexLiveDecisionEvent(string? SourceEventId, string? SourceEventType, string? TriggerId,
    IReadOnlyList<CodexLiveDecisionAnswer> Answers);

/// <summary>A custom voice created from a text description.</summary>
/// <param name="Id">Voice ID (<c>voice_…</c>) for <see cref="CodexRealtimeSessionOptions.LiveCustomVoiceId"/>.</param>
/// <param name="Name">Voice name.</param>
/// <param name="Prompt">Description the voice was created from.</param>
public sealed record CodexPromptVoice(string Id, string Name, string Prompt);

/// <summary>Explicit opt-in Live API fields. Sending them is not evidence of Codex OAuth support.</summary>
internal static class CodexLiveFeatures
{
    public static JsonArray Triggers(IReadOnlyList<CodexLiveDecisionTrigger> triggers)
    {
        if (triggers.Count is < 1 or > 2) throw new ArgumentException("Use one or two bounded transcript triggers.");
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in triggers)
        {
            if (trigger.EventType is not ("session.input_transcript.delta" or "session.output_transcript.delta") || !seen.Add(trigger.EventType))
                throw new ArgumentException("Use distinct documented Live transcript directions.");
            result.Add((JsonNode)new JsonObject { ["type"] = "decisions", ["event_type"] = trigger.EventType,
                ["decisions"] = new JsonObject { ["questions"] = Questions(trigger.Questions) } });
        }
        return result;
    }

    public static JsonArray Questions(IReadOnlyList<CodexLiveChoiceQuestion> questions)
    {
        if (questions.Count is < 1 or > 8) throw new ArgumentException("Use 1..8 choice questions.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var question in questions)
        {
            if (!Identifier(question.Name, 64) || !names.Add(question.Name) ||
                string.IsNullOrWhiteSpace(question.Instructions) || question.Instructions.Length > 2000 ||
                question.Choices.Count is < 2 or > 16 || question.Choices.Any(c => !Identifier(c, 64)) ||
                question.Choices.Distinct(StringComparer.Ordinal).Count() != question.Choices.Count)
                throw new ArgumentException("Questions require unique bounded names, instructions and distinct choices.");
            result.Add((JsonNode)new JsonObject { ["type"] = "choice", ["name"] = question.Name, ["instructions"] = question.Instructions,
                ["choices"] = new JsonArray(question.Choices.Select(c => (JsonNode)new JsonObject { ["value"] = c }).ToArray()) });
        }
        return result;
    }

    public static bool Identifier(string? value, int maximum = 128) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static JsonObject PromptVoice(string name, string prompt, string? scriptHint = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000 ||
            scriptHint is not null && (string.IsNullOrWhiteSpace(scriptHint) || scriptHint.Length is < 20 or > 2000))
            throw new ArgumentException("Supply a bounded voice name, description and optional representative script.");
        var result = new JsonObject { ["type"] = "prompt", ["model"] = "auto", ["name"] = name, ["prompt"] = prompt };
        if (scriptHint is not null) result["script_hint"] = scriptHint;
        return result;
    }

    public static CodexLiveDecisionEvent? ReadDecision(JsonElement payload)
    {
        if (CodexRealtimeProtocol.Text(payload, "type") != "decision.event" ||
            !payload.TryGetProperty("event", out var inner) || CodexRealtimeProtocol.Text(inner, "type") != "decision.completed" ||
            !inner.TryGetProperty("decision", out var decision)) return null;
        var answers = ReadAnswers(decision);
        if (answers is null) return null;
        static string? Id(JsonElement p, string name) => CodexRealtimeProtocol.Text(p, name) is { } value && Identifier(value) ? value : null;
        return new(Id(payload, "source_event_id"), Id(payload, "source_event_type"), Id(payload, "trigger_id"), answers);
    }

    public static IReadOnlyList<CodexLiveDecisionAnswer>? ReadAnswers(JsonElement decision)
    {
        if (decision.ValueKind != JsonValueKind.Object || !decision.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Array || answers.GetArrayLength() is < 1 or > 8) return null;
        var result = new List<CodexLiveDecisionAnswer>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in answers.EnumerateArray())
        {
            var name = CodexRealtimeProtocol.Text(answer, "name"); var choice = CodexRealtimeProtocol.Text(answer, "choice");
            if (CodexRealtimeProtocol.Text(answer, "type") != "choice" || !Identifier(name, 64) || !Identifier(choice, 64) || !names.Add(name ?? "")) return null;
            double? confidence = null;
            if (answer.TryGetProperty("confidence", out var value))
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var probability) || !double.IsFinite(probability) || probability is < 0 or > 1) return null;
                confidence = probability;
            }
            result.Add(new(name ?? "", choice ?? "", confidence));
        }
        return result;
    }
}
