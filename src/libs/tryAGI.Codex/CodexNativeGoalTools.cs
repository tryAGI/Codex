using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Authorization for one model-requested goal mutation. The host must verify explicit
/// user/system intent for creation, the completion audit, three-turn blocked audit, or an explicit
/// user pause request. The model's assertion is not proof of any of those conditions.</summary>
/// <param name="AccountId">Authenticated account.</param>
/// <param name="ThreadId">Account-bound thread.</param>
/// <param name="TurnId">Owning admitted turn.</param>
/// <param name="CallId">Original tool call identity.</param>
/// <param name="ExpectedRevision">Exact journal revision being authorized.</param>
/// <param name="GoalId">Current goal generation, if present.</param>
/// <param name="GoalStatus">Current goal status, if present.</param>
/// <param name="ToolName">Native goal function.</param>
/// <param name="Arguments">Validated arguments, not trusted user intent.</param>
public sealed record CodexGoalToolAuthorization(string AccountId, Guid ThreadId, Guid TurnId, string CallId,
    long ExpectedRevision, Guid? GoalId, string? GoalStatus, string ToolName, JsonElement Arguments);

internal sealed record CodexGoalToolPolicy(
    Func<CodexGoalToolAuthorization, CancellationToken, Task<bool>> Authorize,
    long? MaximumTokenBudget, Func<CodexThreadDocument, Guid, Task> Notify);

internal static class CodexNativeGoalTools
{
    internal const string Get = "get_goal", Create = "create_goal", Update = "update_goal";
    private static readonly JsonElement[] Definitions =
    [
        Parse("""
            {"type":"function","name":"get_goal","description":"Get the current persistent thread goal, its status, budgets, usage and remaining tokens.","strict":false,"parameters":{"type":"object","properties":{},"additionalProperties":false}}
            """),
        Parse("""
            {"type":"function","name":"create_goal","description":"Create an active goal only at explicit user or system/developer request. Do not infer a goal from an ordinary task. Set a positive token_budget only when explicitly requested. An unfinished goal cannot be replaced.","strict":false,"parameters":{"type":"object","properties":{"objective":{"type":"string"},"token_budget":{"type":"integer"}},"required":["objective"],"additionalProperties":false}}
            """),
        Parse("""
            {"type":"function","name":"update_goal","description":"Mark the existing goal complete only after verifying the entire objective; blocked only after the same genuine impasse has persisted for at least three consecutive goal turns; paused only at explicit user request. You cannot resume a goal or set budget or usage limits. Report the final budget usage when complete.","strict":false,"parameters":{"type":"object","properties":{"status":{"type":"string","enum":["complete","blocked","paused"]}},"required":["status"],"additionalProperties":false}}
            """)
    ];

    internal static bool IsGoalCall(CodexToolCall call) => call.Kind == CodexToolCallKind.Function &&
        CodexToolCalls.NormalizeNamespace(call.Namespace) == "functions" && IsName(call.Name);
    private static bool IsName(string name) => name is Get or Create or Update;

    internal static IReadOnlyList<JsonElement> Attach(IReadOnlyList<JsonElement> tools)
    {
        var advertised = CodexToolCalls.Advertised(tools, true);
        if (advertised.Keys.Any(key => key.Namespace == "functions" && IsName(key.Name)))
            throw new ArgumentException("Native goal tools cannot replace a host-advertised tool identity.");
        return [.. tools, .. Definitions];
    }

    internal sealed record Command(string Name, string? Objective, long? Budget, string? Status, JsonElement Arguments);

    internal static Command Prepare(CodexToolCall call, long? maximumBudget)
    {
        if (!IsGoalCall(call) || Encoding.UTF8.GetByteCount(call.Input) > 32_768)
            throw new ArgumentException("Invalid native goal tool call.");
        var args = Parse(call.Input);
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Goal tool requires an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in args.EnumerateObject())
            if (!names.Add(field.Name) || !(call.Name switch
            {
                Create => field.Name is "objective" or "token_budget",
                Update => field.Name == "status",
                _ => false
            })) throw new ArgumentException("Unexpected or duplicate goal tool argument.");

        if (call.Name == Get) return new(Get, null, null, null, args);
        if (call.Name == Update)
        {
            var status = CodexRealtimeProtocol.Text(args, "status");
            if (status is not ("complete" or "blocked" or "paused"))
                throw new ArgumentException("update_goal permits only complete, blocked or paused.");
            return new(Update, null, null, status, args);
        }

        var objective = CodexThreadStore.NormalizeGoalObjective(CodexRealtimeProtocol.Text(args, "objective")
            ?? throw new ArgumentException("Goal objective is required."));
        long? budget = null;
        if (args.TryGetProperty("token_budget", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var requested))
                throw new ArgumentException("Goal token budget must be a positive integer.");
            budget = requested;
        }
        budget ??= maximumBudget;
        CodexThreadStore.ValidateGoalBudget(budget, maximumBudget);
        return new(Create, objective, budget, null, args);
    }

    internal static string? Rejection(Command command, CodexStoredThreadGoal? goal) => command.Name switch
    {
        Create when goal is not null && goal.Status != "complete" => "An unfinished goal already exists; complete it before creating another.",
        Update when goal is null => "No goal exists.",
        _ => null
    };

    internal static CodexToolOutput Error(string message) => new(new JsonObject { ["error"] = message }.ToJsonString());

    internal static CodexToolOutput Result(Guid threadId, CodexStoredThreadGoal? goal, bool completion)
    {
        var remaining = goal is { TokenBudget: { } budget } ? (long?)Math.Max(0, budget - goal.TokensUsed) : null;
        return new(new JsonObject
        {
            ["goal"] = CodexThreadRpc.Goal(threadId, goal),
            ["remainingTokens"] = remaining,
            ["completionBudgetReport"] = completion && goal is { Status: "complete" } &&
                (goal.TokenBudget is not null || goal.TimeUsedSeconds > 0)
                ? "Goal achieved. Report final usage from the structured goal fields, including tokens used and token budget when present and elapsed time when known." : null
        }.ToJsonString());
    }

    internal static JsonElement Request(CodexToolCall call) => Parse(new JsonObject
    {
        ["kind"] = call.Kind.ToString(), ["name"] = call.Name, ["namespace"] = call.Namespace, ["input"] = call.Input
    }.ToJsonString());

    internal static JsonElement Receipt(CodexToolCall call, CodexToolOutput output) => Parse(new JsonObject
    {
        ["type"] = "function_call_output", ["call_id"] = call.Id, ["output"] = output.Content()
    }.ToJsonString());

    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
