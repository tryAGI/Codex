using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host-resolved policy for the two nonblocking user-message tools.
/// Restricted root capabilities include native subagents and host-restricted threads.</summary>
public sealed record CodexAsyncUserMessagePolicy(bool RootCapabilitiesRestricted,
    bool UserInputEnabled, bool SendMessageFeatureEnabled, IReadOnlyCollection<string> SupportedTools);

/// <summary>One async agent-message publication scoped to its exact turn and provider call ID.
/// Publication is not a user response or authorization to perform another action.</summary>
public sealed record CodexAsyncUserMessagePublication(string ThreadId, string TurnId, string CallId, JsonElement Item);

/// <summary>Native contracts for request_user_input_async and send_message_to_user_async.
/// Unlike blocking server requests, these create async agent-message items, return after
/// publication, and leave later user messages to the ordinary turn admission path.</summary>
public static class CodexAsyncUserMessageTools
{
    /// <summary>Current question tool name; the older catalog alias is only a capability marker.</summary>
    public const string RequestUserInput = "request_user_input_async";
    /// <summary>Freeform nonblocking user-message tool name.</summary>
    public const string SendMessage = "send_message_to_user_async";

    private const string QuestionDescription = "Ask the user one or more questions during ongoing work. Use this tool only to request missing information, preferences, constraints, clarification, or approval. The tool returns immediately without ending the turn or waiting for a reply; any reply arrives asynchronously as a new user message. Keep questions concise, self-contained, and easy to understand, using a level of detail appropriate to the user and task. The UI always allows a free-text answer, including when suggested options are provided. A preselected option is not submitted automatically.";
    private const string MessageDescription = "Send a concise message that needs the user's attention during ongoing work. The tool returns immediately without ending the turn or waiting for a reply; any reply arrives asynchronously as a new user message. Use this tool to report a critical blocker or a finding that may change the task's direction, or to answer a user question or status request received while work is still in progress. Use this tool when a message needs the user's immediate attention; use commentary for routine progress and intermediate context. Use clear formatting, such as bolding questions, to make requests easy to notice and answer.";
    private const string QuestionSchema = """
        {"type":"object","properties":{"questions":{"type":"array","minItems":1,
        "description":"One or more self-contained questions to present together, in display order.",
        "items":{"type":"object","properties":{
        "title":{"type":"string","description":"The complete question shown to the user, including any context needed to answer it."},
        "options":{"type":"array","items":{"type":"string"},"minItems":1,
        "description":"Suggested answers, in display order. Put the recommended answer first; the first option is preselected by default. The user can select one option or enter a free-text answer. Do not include an Other option or a free-text placeholder; the UI provides free-text input automatically. Omit options for a free-text-only question."}},
        "required":["title"],"additionalProperties":false}}},
        "required":["questions"],"additionalProperties":false}
        """;
    private const string MessageSchema = """
        {"type":"object","properties":{"message":{"type":"string","description":"The concise question or update to send to the user."}},
        "required":["message"],"additionalProperties":false}
        """;

    /// <summary>Registers only supported, root-authorized tools as DirectModelOnly.
    /// The host may supply already validated model-catalog description and schema overrides.</summary>
    public static IReadOnlyList<CodexRegisteredTool> Register(CodexAsyncUserMessagePolicy policy,
        string? questionDescription = null, JsonElement? questionParameters = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.SupportedTools);
        var result = new List<CodexRegisteredTool>();
        if (IsAvailable(policy, RequestUserInput))
        {
            if (questionParameters is { } supplied && supplied.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Catalog parameters override must be a validated JSON schema object.", nameof(questionParameters));
            result.Add(Registration(RequestUserInput, questionDescription ?? QuestionDescription,
                questionParameters?.GetRawText() ?? QuestionSchema));
        }
        if (IsAvailable(policy, SendMessage))
            result.Add(Registration(SendMessage, MessageDescription, MessageSchema));
        return result;
    }

    /// <summary>Validates the original admitted function call and prepares the App Server
    /// agentMessage item. No user response is synthesized and no notification is sent here.</summary>
    public static CodexAsyncUserMessagePublication Prepare(CodexAsyncUserMessagePolicy policy,
        string threadId, string turnId, CodexToolCall call)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.SupportedTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrWhiteSpace(call.Id);
        if (call.Kind != CodexToolCallKind.Function ||
            CodexToolCalls.NormalizeNamespace(call.Namespace) != "functions" || !IsAvailable(policy, call.Name))
            throw new UnauthorizedAccessException("Async user-message tool is not available for this turn.");

        using var parsed = JsonDocument.Parse(call.Input);
        var args = parsed.RootElement;
        string text;
        JsonArray? questions = null;
        if (call.Name == RequestUserInput)
        {
            RequireMembers(args, "questions");
            if (!args.TryGetProperty("questions", out var supplied) || supplied.ValueKind != JsonValueKind.Array ||
                supplied.GetArrayLength() == 0)
                throw new CodexProtocolException("questions must not be empty");
            questions = new JsonArray();
            var messages = new List<string>();
            foreach (var question in supplied.EnumerateArray())
            {
                RequireMembers(question, "title", "options");
                var title = Nonempty(question, "title", "question titles must not be empty");
                var lines = new List<string> { title };
                JsonArray? options = null;
                if (question.TryGetProperty("options", out var choices) && choices.ValueKind != JsonValueKind.Null)
                {
                    if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                        throw new CodexProtocolException("options must contain at least one non-empty answer");
                    options = new JsonArray();
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(choice.GetString()))
                            throw new CodexProtocolException("options must contain at least one non-empty answer");
                        var option = choice.GetString()!;
                        lines.Add($"- {option}");
                        options.Add((JsonNode?)JsonValue.Create(option));
                    }
                }
                questions.Add((JsonNode)new JsonObject { ["title"] = title, ["options"] = options });
                messages.Add(string.Join("\n", lines));
            }
            text = string.Join("\n\n", messages);
        }
        else
        {
            RequireMembers(args, "message");
            text = Nonempty(args, "message", "message must not be empty").Trim();
        }

        return new(threadId, turnId, call.Id, Json(new JsonObject
        {
            ["type"] = "agentMessage", ["id"] = call.Id, ["text"] = text,
            ["phase"] = "final_answer", ["delivery"] = "async", ["questions"] = questions,
            ["moderation"] = null, ["memoryCitation"] = null,
        }));
    }

    /// <summary>Returns accepted only after the host acknowledges idempotent publication of
    /// the exact item (item/started and item/completed). The host callback must authenticate,
    /// authorize, persist and deduplicate by thread/turn/call. No reply is awaited or invented.</summary>
    public static async Task<CodexToolOutput> ExecuteAsync(CodexAsyncUserMessagePolicy policy,
        string threadId, string turnId, CodexToolCall call,
        Func<CodexAsyncUserMessagePublication, CancellationToken, Task<bool>> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        var publication = Prepare(policy, threadId, turnId, call);
        if (!await publish(publication, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Async user-message publication was not admitted.");
        return new CodexToolOutput("""{"accepted":true}""");
    }

    private static bool IsAvailable(CodexAsyncUserMessagePolicy policy, string name) =>
        !policy.RootCapabilitiesRestricted && (name switch
        {
            RequestUserInput => policy.UserInputEnabled && (policy.SupportedTools.Contains(RequestUserInput, StringComparer.Ordinal) ||
                policy.SupportedTools.Contains("send_user_message_async", StringComparer.Ordinal)),
            SendMessage => policy.SendMessageFeatureEnabled || policy.SupportedTools.Contains(SendMessage, StringComparer.Ordinal),
            _ => false,
        });

    private static CodexRegisteredTool Registration(string name, string description, string schema) =>
        new(new CodexToolIdentity(null, name), Json(new JsonObject
        {
            ["type"] = "function", ["name"] = name, ["description"] = description,
            ["strict"] = false, ["parameters"] = JsonNode.Parse(schema),
        }), CodexToolExposure.DirectModelOnly);

    private static string Nonempty(JsonElement value, string name, string error) =>
        value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(member.GetString()) ? member.GetString()! : throw new CodexProtocolException(error);

    private static void RequireMembers(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Async tool arguments must be an object.");
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !members.Add(property.Name))
                throw new CodexProtocolException("Unexpected or duplicate async tool argument.");
    }

    private static JsonElement Json(JsonObject value)
    {
        using var parsed = JsonDocument.Parse(value.ToJsonString());
        return parsed.RootElement.Clone();
    }
}
