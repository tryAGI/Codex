using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Host-selected hook execution context, including exact execution authority.</summary>
public sealed record CodexHookContext(string AccountId, string ConnectionId, string ThreadId,
    string? TurnId, string EventId, string EventName, string WorkingDirectory,
    string? EnvironmentId = null, string? ToolName = null, string? ToolUseId = null,
    bool IsInternalSubagent = false);

/// <summary>Resolved hook declaration. The host resolves configuration, trust and matcher
/// aliases before supplying this descriptor. Configuration is not execution authority.</summary>
public sealed record CodexHookDefinition(string Id, string EventName, string HandlerType,
    string SourcePath, string Source, string TrustStatus, long DisplayOrder,
    string Scope = "turn", string ExecutionMode = "sync", bool Builtin = false,
    string? EnvironmentId = null);

/// <summary>One exact hook invocation for independent host authorization and dispatch.</summary>
public sealed record CodexHookInvocation(CodexHookContext Context, CodexHookDefinition Definition,
    string RunId, JsonElement Payload);

/// <summary>Result of the host's authorized handler execution. Output is parsed as hook JSON,
/// not as executable code or permission authority. Exit code 2 is a blocking command outcome.</summary>
public sealed record CodexHookExecutionResult(int ExitCode, JsonElement? Output = null);

/// <summary>One model/UI-facing hook message.</summary>
public sealed record CodexHookOutputEntry(string Kind, string Text);

/// <summary>Result of the synchronous lifecycle. PermissionAdvice is advisory only;
/// a caller must use its ordinary permission/approval pipeline before executing any tool.</summary>
public sealed record CodexHookOutcome(bool Blocked, bool StopRequested, bool RequireMoreWork,
    string? Reason, JsonElement? UpdatedInput, JsonElement? UpdatedMcpToolOutput,
    string? PermissionAdvice, IReadOnlyList<string> AdditionalContexts,
    IReadOnlyList<JsonElement> Runs)
{
    /// <summary>Whether a protected tool action may proceed to normal host admission.</summary>
    public bool CanProceed => !Blocked && !StopRequested;
}

/// <summary>Host-executed synchronous hook lifecycle. No process, MCP request or agent
/// is executed by this class. Every invocation is authorized separately; any tool input
/// rewrite must be re-admitted by the ordinary host policy. Async scheduling is explicit
/// and not implemented by this synchronous runtime.</summary>
public sealed class CodexHookRuntime
{
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "PreModelRequest", "PostModelRequest", "PreToolUse", "PermissionRequest", "PostToolUse",
        "PreCompact", "PostCompact", "SessionStart", "SessionEnd", "UserPromptSubmit",
        "SubagentStart", "SubagentStop", "Stop", "Interrupt",
    };
    private readonly Func<CodexHookInvocation, CancellationToken, ValueTask<bool>> authorize;
    private readonly Func<CodexHookInvocation, CancellationToken, Task<CodexHookExecutionResult>> execute;
    private readonly Func<string, JsonElement, CancellationToken, Task> notify;
    private readonly TimeProvider clock;

    /// <summary>Requires explicit trusted host callbacks for authorization, execution
    /// and event publication. The execution adapter must respect cancellation and must
    /// never dispatch a duplicate RunId after an ambiguous outcome.</summary>
    public CodexHookRuntime(Func<CodexHookInvocation, CancellationToken, ValueTask<bool>> authorize,
        Func<CodexHookInvocation, CancellationToken, Task<CodexHookExecutionResult>> execute,
        Func<string, JsonElement, CancellationToken, Task> notify, TimeProvider? clock = null)
    {
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Runs matching host-resolved synchronous hooks in display order. A protected
    /// pre-tool/permission event fails closed on authorization, execution or output failure.
    /// Every visible run has correlated started and completed notifications. Builtins are
    /// included in returned summaries but not user-visible lifecycle notifications.</summary>
    public async Task<CodexHookOutcome> RunAsync(CodexHookContext context,
        IReadOnlyList<CodexHookDefinition> declarations, JsonElement payload,
        Func<CodexHookContext, JsonElement, CancellationToken, ValueTask<bool>>? readmitToolInput = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(declarations);
        RequireObject(payload);
        var selected = declarations.Where(d => d.EventName == context.EventName &&
            (d.EnvironmentId is null || d.EnvironmentId == context.EnvironmentId))
            .OrderBy(d => d.DisplayOrder).ToArray();
        if (selected.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Duplicate hook identity.", nameof(declarations));
        foreach (var definition in selected) ValidateDefinition(definition);
        if (context.IsInternalSubagent) selected = [];
        var original = payload.Clone();
        var current = original;
        JsonElement? updatedInput = null;
        JsonElement? updatedOutput = null;
        string? permission = null;
        string? reason = null;
        var blocked = false;
        var stopped = false;
        var moreWork = false;
        var contexts = new List<string>();
        var runs = new List<JsonElement>();
        var protectedAction = context.EventName is "PreToolUse" or "PermissionRequest";

        foreach (var definition in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runId = $"{context.EventId}:{definition.Id}";
            var invocation = new CodexHookInvocation(context, definition, runId, current);
            var start = clock.GetUtcNow();
            var visible = !definition.Builtin && definition.ExecutionMode == "sync";
            if (visible)
                await notify("hook/started", Notification(context, Summary(invocation, "running", null, start, null, [])),
                    cancellationToken).ConfigureAwait(false);
            var entries = new List<CodexHookOutputEntry>();
            var status = "completed";
            string? message = null;
            try
            {
                if (definition.ExecutionMode != "sync")
                    throw new NotSupportedException("Asynchronous hooks require a host scheduling contract.");
                if (definition.TrustStatus is "untrusted" or "modified" ||
                    !await authorize(invocation, cancellationToken).ConfigureAwait(false))
                    throw new UnauthorizedAccessException("Hook execution was not authorized.");
                cancellationToken.ThrowIfCancellationRequested();
                var result = await execute(invocation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.ExitCode == 2)
                {
                    message = "Hook returned a blocking exit code.";
                    if (context.EventName is "Stop" or "SubagentStop") moreWork = true;
                    else blocked = true;
                    reason ??= message;
                    status = "blocked";
                    entries.Add(new("feedback", message));
                }
                else if (result.ExitCode != 0)
                    throw new IOException("Hook handler failed.");
                else if (result.Output is { } output)
                {
                    var parsed = ParseOutput(context.EventName, output);
                    if (parsed.SystemMessage is { } systemMessage) entries.Add(new("warning", systemMessage));
                    if (parsed.Context is { } additional)
                    {
                        contexts.Add(additional);
                        entries.Add(new("context", additional));
                    }
                    if (parsed.Permission is { } advice)
                    {
                        // Denial and ask dominate allow, and allow is never an admission.
                        if (permission != "deny" && (permission != "ask" || advice == "deny")) permission = advice;
                        if (advice == "deny") blocked = true;
                    }
                    if (parsed.Block)
                    {
                        if (context.EventName is "Stop" or "SubagentStop") moreWork = true;
                        else blocked = true;
                    }
                    if (parsed.Stop) stopped = true;
                    if (parsed.Input is { } proposed && !blocked && !stopped)
                    {
                        // Hook changes are not trusted merely because the hook itself ran.
                        if (readmitToolInput is null ||
                            !await readmitToolInput(context, proposed, cancellationToken).ConfigureAwait(false))
                        {
                            blocked = true;
                            reason ??= "Updated tool input was not admitted.";
                        }
                        else
                        {
                            updatedInput = proposed;
                            var next = JsonNode.Parse(current.GetRawText())!.AsObject();
                            next["tool_input"] = JsonNode.Parse(proposed.GetRawText());
                            current = Element(next);
                        }
                    }
                    if (parsed.McpOutput is { } replacement && !blocked && !stopped)
                        updatedOutput = replacement;
                    if (parsed.Reason is { } why) reason ??= why;
                    if (blocked || moreWork) status = "blocked";
                    else if (stopped) status = "stopped";
                    message = reason;
                    if (message is not null && status is "blocked" or "stopped")
                        entries.Add(new(status == "stopped" ? "stop" : "feedback", message));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation is not a successful execution result.
                var completed = Summary(invocation, "stopped", "Hook was cancelled.", start, clock.GetUtcNow(), []);
                if (visible) await notify("hook/completed", Notification(context, completed), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                NotSupportedException or JsonException or ArgumentException or OperationCanceledException)
            {
                status = "failed";
                message = "Hook execution or output was not accepted.";
                entries.Add(new("error", message));
                if (protectedAction) { blocked = true; reason ??= message; }
            }
            var summary = Summary(invocation, status, message, start, clock.GetUtcNow(), entries);
            runs.Add(summary);
            if (visible)
                await notify("hook/completed", Notification(context, summary), cancellationToken).ConfigureAwait(false);
            if (blocked || stopped) break;
        }

        return new(blocked, stopped, moreWork, reason,
            blocked || stopped ? null : updatedInput, blocked || stopped ? null : updatedOutput, permission,
            contexts.AsReadOnly(), runs.AsReadOnly());
    }

    private sealed record Parsed(bool Block, bool Stop, string? Reason, string? SystemMessage,
        string? Context, string? Permission, JsonElement? Input, JsonElement? McpOutput);

    private static Parsed ParseOutput(string eventName, JsonElement output)
    {
        RequireObject(output);
        var supportsBlock = eventName is "PreToolUse" or "PostToolUse" or "UserPromptSubmit" or "Stop" or "SubagentStop";
        var supportsSpecific = eventName is "PreToolUse" or "PermissionRequest" or "PostToolUse" or
            "SessionStart" or "SubagentStart" or "UserPromptSubmit";
        foreach (var field in output.EnumerateObject())
        {
            var allowed = field.Name == "systemMessage" ||
                eventName != "Interrupt" && (field.Name is "continue" or "stopReason" or "suppressOutput" ||
                    supportsBlock && field.Name is "decision" or "reason" ||
                    supportsSpecific && field.Name == "hookSpecificOutput");
            if (!allowed) throw new CodexProtocolException("Unsupported hook output member.");
        }
        var stop = !Boolean(output, "continue", true);
        _ = Boolean(output, "suppressOutput", false);
        var decision = Text(output, "decision");
        var reason = Text(output, "reason") ?? Text(output, "stopReason");
        if (decision is not null && decision != "block" && !(eventName == "PreToolUse" && decision == "approve"))
            throw new CodexProtocolException("Unsupported hook decision.");
        if (decision == "block" && string.IsNullOrWhiteSpace(reason))
            throw new CodexProtocolException("Blocking hook output requires a reason.");
        var block = decision == "block";
        string? permission = null;
        string? additional = null;
        JsonElement? input = null;
        JsonElement? mcpOutput = null;
        if (output.TryGetProperty("hookSpecificOutput", out var specific) && specific.ValueKind != JsonValueKind.Null)
        {
            RequireObject(specific);
            if (Text(specific, "hookEventName") != eventName)
                throw new CodexProtocolException("Hook output event correlation mismatch.");
            foreach (var property in specific.EnumerateObject())
            {
                if (property.Name == "hookEventName") continue;
                var allowed = eventName switch
                {
                    "PreToolUse" => property.Name is "permissionDecision" or "permissionDecisionReason" or "updatedInput" or "additionalContext",
                    "PermissionRequest" => property.Name == "decision",
                    "PostToolUse" => property.Name is "additionalContext" or "updatedMCPToolOutput",
                    _ => property.Name == "additionalContext",
                };
                if (!allowed) throw new CodexProtocolException("Unexpected hook-specific output member.");
            }
            additional = Text(specific, "additionalContext");
            if (eventName == "PreToolUse")
            {
                permission = Text(specific, "permissionDecision");
                if (permission is not (null or "allow" or "deny" or "ask"))
                    throw new CodexProtocolException("Invalid hook permission advice.");
                reason ??= Text(specific, "permissionDecisionReason");
                input = OptionalValue(specific, "updatedInput");
                if (input is { } proposed) RequireObject(proposed);
            }
            else if (eventName == "PermissionRequest" && OptionalValue(specific, "decision") is { } advice)
            {
                RequireObject(advice);
                foreach (var property in advice.EnumerateObject())
                    if (property.Name is not ("behavior" or "message" or "interrupt" or "updatedInput" or "updatedPermissions"))
                        throw new CodexProtocolException("Unsupported permission decision member.");
                if (OptionalValue(advice, "updatedInput") is not null || OptionalValue(advice, "updatedPermissions") is not null ||
                    Boolean(advice, "interrupt", false))
                    throw new CodexProtocolException("Permission hook cannot rewrite input or permissions.");
                permission = Text(advice, "behavior");
                if (permission is not ("allow" or "deny")) throw new CodexProtocolException("Invalid permission hook advice.");
                reason ??= Text(advice, "message");
            }
            else if (eventName == "PostToolUse") mcpOutput = OptionalValue(specific, "updatedMCPToolOutput");
        }
        return new(block, stop, reason, Text(output, "systemMessage"), additional, permission, input, mcpOutput);
    }

    private static void ValidateContext(CodexHookContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var identity in new[] { context.AccountId, context.ConnectionId, context.ThreadId, context.EventId, context.WorkingDirectory })
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (!Events.Contains(context.EventName)) throw new ArgumentException("Unknown hook event.", nameof(context));
        if (context.EventName is "PreToolUse" or "PermissionRequest" or "PostToolUse")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(context.TurnId);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolName);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolUseId);
        }
    }

    private static void ValidateDefinition(CodexHookDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.SourcePath);
        if (definition.Id.Contains(':') || definition.HandlerType is not ("command" or "mcpTool" or "prompt" or "agent") ||
            definition.TrustStatus is not ("managed" or "trusted" or "untrusted" or "modified") ||
            definition.Scope is not ("thread" or "turn") || definition.ExecutionMode is not ("sync" or "async") ||
            definition.Source is not ("system" or "user" or "project" or "mdm" or "sessionFlags" or "plugin" or
                "cloudRequirements" or "cloudManagedConfig" or "legacyManagedConfigFile" or "legacyManagedConfigMdm" or "unknown"))
            throw new ArgumentException("Invalid resolved hook declaration.");
    }

    private static JsonElement Summary(CodexHookInvocation call, string status, string? message,
        DateTimeOffset started, DateTimeOffset? completed, IReadOnlyList<CodexHookOutputEntry> entries)
    {
        var definition = call.Definition;
        var output = new JsonArray();
        foreach (var entry in entries) output.Add((JsonNode)new JsonObject { ["kind"] = entry.Kind, ["text"] = entry.Text });
        return Element(new JsonObject
        {
            ["id"] = call.RunId, ["eventName"] = char.ToLowerInvariant(definition.EventName[0]) + definition.EventName[1..],
            ["handlerType"] = definition.HandlerType, ["executionMode"] = definition.ExecutionMode,
            ["scope"] = definition.Scope, ["sourcePath"] = definition.SourcePath, ["source"] = definition.Source,
            ["displayOrder"] = definition.DisplayOrder, ["status"] = status, ["statusMessage"] = message,
            ["startedAt"] = started.ToUnixTimeMilliseconds(), ["completedAt"] = completed?.ToUnixTimeMilliseconds(),
            ["durationMs"] = completed is { } end ? Math.Max(0, (long)(end - started).TotalMilliseconds) : null,
            ["entries"] = output,
        });
    }

    private static JsonElement Notification(CodexHookContext context, JsonElement run) => Element(new JsonObject
    {
        ["threadId"] = context.ThreadId, ["turnId"] = context.TurnId, ["run"] = JsonNode.Parse(run.GetRawText()),
    });

    private static JsonElement Element(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Hook payload must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
            if (!names.Add(member.Name)) throw new CodexProtocolException("Duplicate hook output member.");
    }

    private static JsonElement? OptionalValue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var member) && member.ValueKind != JsonValueKind.Null ? member.Clone() : null;

    private static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Null) return null;
        return member.ValueKind == JsonValueKind.String ? member.GetString() : throw new CodexProtocolException("Invalid hook text.");
    }

    private static bool Boolean(JsonElement value, string name, bool fallback)
    {
        if (!value.TryGetProperty(name, out var member)) return fallback;
        return member.ValueKind is JsonValueKind.True or JsonValueKind.False ? member.GetBoolean() :
            throw new CodexProtocolException("Invalid hook boolean.");
    }
}
