using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed record CodexThreadRun(CodexThreadDocument Thread, Guid TurnId, string OwnerId,
    CodexResponseOptions Options, IReadOnlyList<JsonElement> Input, CodexAgentControl Control,
    Func<JsonElement, CancellationToken, Task> ValidatedItem)
{
    public Func<CodexResponseEvent, CancellationToken, Task>? ObserveProvider { get; init; }
    public Func<string, CancellationToken, Task<CodexToolOutput>>? ExecutePlan { get; init; }
    public Func<CodexWorkspacePatchResult, Task>? AppliedPatch { get; init; }
}

public sealed class CodexThreadRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Account-bound native Thread/Turn RPC host. The embedding host supplies authenticated
/// storage, resolved defaults, budgeted execution and notifications. Unsupported host capabilities
/// are rejected before writes; optional Rust surfaces are never silently acknowledged.</summary>
public sealed partial class CodexThreadRpc(
    CodexThreadStore store,
    Func<CancellationToken, Task<IReadOnlyList<CodexThreadDocument>>> list,
    JsonElement defaults,
    Func<CodexThreadRun, Func<CodexThreadDocument, Task>, CancellationToken, Task<CodexThreadDocument>> execute,
    Func<string, JsonElement, Task> notify,
    Action<CodexResponseOptions>? validateOptions = null,
    Action<IReadOnlyList<JsonElement>>? validateInput = null,
    int maximumConcurrentTurns = 4,
    IReadOnlyList<JsonElement>? tools = null,
    Func<CodexThreadConfigurationContext, CancellationToken, Task<CodexThreadConfigurationSnapshot>>? configurationSource = null)
{
    private readonly ConcurrentDictionary<Guid, byte> loaded = new();
    private readonly ConcurrentDictionary<Guid, Running> active = new();
    private readonly SemaphoreSlim admission = new(1, 1);
    private bool stopping;
    public static bool Handles(string method) => method is "thread/start" or "thread/resume" or "thread/fork"
        or "thread/list" or "thread/loaded/list" or "thread/read" or "thread/turns/list" or "thread/items/list"
        or "turn/start" or "turn/steer" or "turn/interrupt" or "turn/settings/update";

    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters, CancellationToken ct = default)
    {
        if (Volatile.Read(ref stopping) && method is not ("thread/list" or "thread/loaded/list" or "thread/read" or "thread/turns/list" or "thread/items/list"))
            throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
        var p = parameters is { ValueKind: JsonValueKind.Object } value ? value : parameters is null
            ? Json("{}") : throw Invalid("RPC parameters must be an object.");
        if (System.Text.Encoding.UTF8.GetByteCount(p.GetRawText()) > 1_048_576) throw Invalid("Native RPC parameters exceed their bound.");
        try
        {
            return method switch
            {
                "thread/start" => await CreateAsync(p, ct).ConfigureAwait(false),
                "thread/resume" => await ResumeAsync(p, ct).ConfigureAwait(false),
                "thread/fork" => await ForkAsync(p, ct).ConfigureAwait(false),
                "thread/list" => await ListAsync(p, false, ct).ConfigureAwait(false),
                "thread/loaded/list" => await ListAsync(p, true, ct).ConfigureAwait(false),
                "thread/read" => await ReadAsync(p, ct).ConfigureAwait(false),
                "thread/turns/list" => await HistoryAsync(p, false, ct).ConfigureAwait(false),
                "thread/items/list" => await HistoryAsync(p, true, ct).ConfigureAwait(false),
                "turn/start" => await StartAsync(p, ct).ConfigureAwait(false),
                "turn/steer" => Steer(p),
                "turn/interrupt" => await InterruptAsync(p, ct).ConfigureAwait(false),
                "turn/settings/update" => Update(p),
                _ => throw new CodexThreadRpcException(-32601, "Native method not found.")
            };
        }
        catch (KeyNotFoundException) { throw new CodexThreadRpcException(-32001, "Native thread or turn not found in this account."); }
        catch (CodexThreadConflictException) { throw new CodexThreadRpcException(-32009, "Native thread revision changed; re-read, do not replay."); }
        catch (ArgumentException error) { throw Invalid(error.Message); }
        catch (InvalidOperationException error) { throw new CodexThreadRpcException(-32000, error.Message); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Running[] pending;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try { Volatile.Write(ref stopping, true); pending = active.Values.ToArray(); }
        finally { admission.Release(); }
        foreach (var running in pending) running.Control.Interrupt();
        await Task.WhenAll(pending.Select(r => r.Completion)).WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> CreateAsync(JsonElement p, CancellationToken ct)
    {
        var settings = Object(await ResolveLifecycleAsync("thread/start", p, defaults, ["model", "modelProvider", "cwd", "baseInstructions", "developerInstructions", "approvalPolicy", "sandbox", "approvalsReviewer", "serviceTier", "personality", "config", "ephemeral", "serviceName", "sessionStartSource", "threadSource"], null, ct).ConfigureAwait(false));
        var id = Guid.CreateVersion7();
        settings["nativeSessionId"] = id.ToString();
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
            var thread = await store.CreateAsync(id, Element(settings), ct).ConfigureAwait(false);
            loaded.TryAdd(thread.Id, 0);
            return Lifecycle(thread, false);
        }
        finally { admission.Release(); }
    }

    private async Task<JsonElement> ResumeAsync(JsonElement p, CancellationToken ct)
    {
        string[] fields = ["threadId", "excludeTurns", "model", "modelProvider", "cwd", "baseInstructions", "developerInstructions", "approvalPolicy", "sandbox", "approvalsReviewer", "serviceTier", "personality", "config"];
        ValidateFields(p, fields);
        var thread = await store.ReadAsync(Id(p, "threadId"), ct).ConfigureAwait(false);
        if (Flag(thread.Settings, "ephemeral") && !Flag(p, "excludeTurns")) throw Invalid("Ephemeral resume requires excludeTurns.");
        var rejoin = active.ContainsKey(thread.Id);
        var settings = rejoin ? Resolve(p, thread.Settings, fields)
            : await ResolveLifecycleAsync("thread/resume", p, thread.Settings, fields, thread.Id, ct).ConfigureAwait(false);
        // Source I/O never holds the turn admission gate. Recheck identity/state before CAS.
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
            var current = await store.ReadAsync(thread.Id, ct).ConfigureAwait(false);
            if (current.Turns.LastOrDefault()?.Status == "inProgress" && !active.ContainsKey(thread.Id))
                throw new CodexThreadRpcException(-32010, "Recorded execution owner is not this host; explicit owner reconciliation is required.");
            if (!rejoin && !active.ContainsKey(thread.Id))
            {
                if (current.Revision != thread.Revision) throw new CodexThreadConflictException();
                if (!JsonElement.DeepEquals(settings, current.Settings))
                    current = await store.UpdateSettingsAsync(current.Id, current.Revision, settings, ct).ConfigureAwait(false);
            }
            loaded.TryAdd(current.Id, 0);
            return Lifecycle(current, !Flag(p, "excludeTurns"));
        }
        finally { admission.Release(); }
    }

    private async Task<JsonElement> ForkAsync(JsonElement p, CancellationToken ct)
    {
        string[] fields = ["threadId", "lastTurnId", "beforeTurnId", "excludeTurns", "model", "modelProvider", "cwd",
            "baseInstructions", "developerInstructions", "approvalPolicy", "sandbox", "approvalsReviewer", "serviceTier", "config", "ephemeral", "threadSource"];
        ValidateFields(p, fields);
        if (p.TryGetProperty("ephemeral", out var transient) && transient.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("Fork ephemeral must be a boolean when present.");
        var source = await store.ReadAsync(Id(p, "threadId"), ct).ConfigureAwait(false);
        if (Flag(source.Settings, "ephemeral")) throw Invalid("Ephemeral threads have no stored fork source.");
        if (Flag(p, "ephemeral") && !Flag(p, "excludeTurns")) throw Invalid("Ephemeral paginated forks require excludeTurns.");
        var settingsNode = Object(await ResolveLifecycleAsync("thread/fork", p, source.Settings, fields, source.Id, ct).ConfigureAwait(false));
        settingsNode["ephemeral"] = Flag(p, "ephemeral");
        var settings = Element(settingsNode);
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
            var next = await store.ForkAsync(source.Id, source.Revision, Guid.CreateVersion7(),
                OptionalId(p, "lastTurnId"), OptionalId(p, "beforeTurnId"), ct, settings).ConfigureAwait(false);
            loaded.TryAdd(next.Id, 0);
            return Lifecycle(next, !Flag(p, "excludeTurns"));
        }
        finally { admission.Release(); }
    }

    private JsonElement Lifecycle(CodexThreadDocument thread, bool includeTurns)
    {
        var settings = Object(thread.Settings);
        return Element(new JsonObject
        {
            ["thread"] = Thread(thread, includeTurns), ["model"] = settings["model"]?.DeepClone(),
            ["modelProvider"] = settings["modelProvider"]?.DeepClone(), ["cwd"] = settings["cwd"]?.DeepClone(),
            ["approvalPolicy"] = settings["approvalPolicy"]?.DeepClone(), ["sandbox"] = new JsonObject { ["type"] = "readOnly" },
            ["approvalsReviewer"] = settings["approvalsReviewer"]?.DeepClone(), ["instructionSources"] = new JsonArray(),
            ["reasoningEffort"] = settings["effort"]?.DeepClone(), ["serviceTier"] = settings["serviceTier"]?.DeepClone()
        });
    }

    private static JsonElement Resolve(JsonElement p, JsonElement baseline, string[] allowed)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(p.GetRawText()) > 1_048_576) throw Invalid("Thread settings exceed their bound.");
        ValidateFields(p, allowed);
        var settings = Object(baseline);
        ApplyConfig(settings, p);
        foreach (var name in new[] { "model", "modelProvider", "cwd", "baseInstructions", "developerInstructions", "approvalPolicy", "sandbox", "approvalsReviewer", "serviceTier", "personality" })
            if (p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                if (v.ValueKind != JsonValueKind.String) throw Invalid("Unsupported settings value: " + name);
                settings[name] = JsonNode.Parse(v.GetRawText());
            }
        // Nullable serviceTier distinguishes an omitted override from explicitly clearing it.
        if (p.TryGetProperty("serviceTier", out var tier) && tier.ValueKind == JsonValueKind.Null) settings["serviceTier"] = null;
        foreach (var field in new[] { "serviceName", "sessionStartSource", "threadSource" })
            if (Text(p, field) is { } metadata)
            {
                if (string.IsNullOrWhiteSpace(metadata) || metadata.Length > 512 || metadata.Any(char.IsControl)) throw Invalid("Invalid thread metadata: " + field);
                if (field == "sessionStartSource" && metadata is not ("startup" or "clear")) throw Invalid("Invalid sessionStartSource.");
                settings[field] = metadata;
            }
        if (p.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind != JsonValueKind.Null)
            settings["ephemeral"] = Flag(p, "ephemeral");
        if (settings["modelProvider"]?.GetValue<string>() != "openai" || settings["sandbox"]?.GetValue<string>() != "read-only"
            || settings["approvalPolicy"]?.GetValue<string>() != "never" || settings["approvalsReviewer"]?.GetValue<string>() != "user")
            throw Invalid("This native host exposes a read-only policy; unsupported execution policies require explicit host integration.");
        foreach (var name in new[] { "model", "cwd" })
            if (settings[name] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Any(char.IsControl))
                throw Invalid("Resolved model/cwd must be bounded nonempty strings.");
        if (Text(Element(settings), "personality") is { } personality && personality is not ("none" or "friendly" or "pragmatic"))
            throw Invalid("Invalid deprecated personality value.");
        // The pinned protocol retains this setting but explicitly no longer selects a style.
        var cwd = settings["cwd"] is JsonValue cwdValue && cwdValue.TryGetValue<string>(out var cwdText)
            ? cwdText : throw Invalid("Missing cwd.");
        if (!Path.IsPathFullyQualified(cwd)) throw Invalid("Native cwd must be absolute.");
        // Validate the resolved config and typed RPC layers together before CAS.
        _ = Apply(new CodexResponseOptions(Text(Element(settings), "model") ?? "validation", "validation"), Element(settings));
        if (Text(Element(settings), "verbosity") is { } verbosity && verbosity is not ("low" or "medium" or "high"))
            throw Invalid("Invalid response verbosity.");
        return Element(settings);
    }

    private static void ValidateFields(JsonElement p, string[] names)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in p.EnumerateObject())
            if (!found.Add(field.Name) || !names.Contains(field.Name, StringComparer.Ordinal))
                throw Invalid("Unknown or unsupported native field: " + field.Name);
    }
    private static Guid Id(JsonElement p, string name) => OptionalId(p, name) ?? throw Invalid("Required identity: " + name);
    private static Guid? OptionalId(JsonElement p, string name) => Text(p, name) is { } text
        ? Guid.TryParse(text, out var id) && id != Guid.Empty ? id : throw Invalid("Invalid identity: " + name) : null;
    private static string? Text(JsonElement p, string name) => p.TryGetProperty(name, out var v)
        ? v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : throw Invalid("Expected string: " + name) : null;
    private static bool Flag(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
        ? v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Invalid("Expected boolean: " + name) : false;
    private static JsonObject Object(JsonElement value) => JsonNode.Parse(value.GetRawText()) as JsonObject ?? throw Invalid("Expected object.");
    private static JsonElement Element(JsonNode node) { using var d = JsonDocument.Parse(node.ToJsonString()); return d.RootElement.Clone(); }
    private static JsonElement Json(string text) { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}
