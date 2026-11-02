using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed partial class CodexThreadRpc
{
    private sealed class Running(Guid id, CodexResponseOptions options)
    {
        internal Guid Id { get; } = id;
        internal CodexResponseOptions Options { get; } = options;
        internal CodexAgentControl Control { get; } = new();
        internal TaskCompletionSource<CodexThreadDocument> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal HashSet<string> StartedItems { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CompletedItems { get; } = new(StringComparer.Ordinal);
        internal Func<CodexThreadRun, Func<CodexThreadDocument, Task>, CancellationToken, Task<CodexThreadDocument>>? ExecuteOwned { get; init; }
        internal Exception? Failure { get; set; }
        internal JsonElement? ConfigurationRequirements { get; init; }
    }

    private long notificationFailures;
    /// <summary>Notifications that could not be delivered.</summary>
    public long NotificationFailures => Interlocked.Read(ref notificationFailures);

    private async Task<JsonElement> StartAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "input", "rootTurnId", "parentTurnId", "model", "effort", "summary", "outputSchema", "serviceTier", "serviceTierForTurn", "personality",
            "cwd", "approvalPolicy", "approvalsReviewer", "sandboxPolicy", "clientUserMessageId", "disabledPluginIds", "toolOutput", "turnTrigger"]);
        var input = Input(p);
        var clientId = InputIdentity(p, "clientUserMessageId");
        var trigger = InputIdentity(p, "turnTrigger");
        if (Text(p, "personality") is { } personality && personality is not ("none" or "friendly" or "pragmatic"))
            throw Invalid("Invalid deprecated personality value.");
        validateInput?.Invoke(input);
        var id = Id(p, "threadId");
        Running current;
        await admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping)) throw new CodexThreadRpcException(-32010, "Native thread host is draining.");
            if (!loaded.ContainsKey(id)) throw Invalid("Resume the native thread before starting a turn.");
            if (active.TryGetValue(id, out var running))
            {
                // No incompatible overrides may be silently ignored by start-or-steer.
                if (p.EnumerateObject().Any(x => x.Name is not ("threadId" or "input" or "clientUserMessageId" or "turnTrigger" or "toolOutput")))
                    throw Invalid("Active start-or-steer does not accept settings overrides; use turn/settings/update.");
                running.Control.Steer(input, clientId);
                return Element(new JsonObject { ["turn"] = Turn((await store.ReadAsync(id, ct).ConfigureAwait(false)).Turns.Single(t => t.Id == running.Id), "notLoaded") });
            }
            var thread = await store.ReadAsync(id, ct).ConfigureAwait(false);
            var effectiveSettings = TurnSettings(thread.Settings, p);
            if (maximumConcurrentTurns is < 1 or > 64 || active.Count >= maximumConcurrentTurns)
                throw new CodexThreadRpcException(-32010, "Native thread host concurrent-turn capacity is exhausted.");
            if (p.TryGetProperty("outputSchema", out var requestedSchema) && requestedSchema.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                throw Invalid("Output schema must be an object.");
            var turnId = Guid.CreateVersion7();
            var instructions = ThreadInstructions(thread.Settings);
            var options = Apply(new CodexResponseOptions(Text(thread.Settings, "model") ?? throw Invalid("Missing model."), instructions)
            {
                Tools = tools ?? [],
                ReasoningEffort = Text(thread.Settings, "effort") ?? "low",
                ReasoningSummary = Text(thread.Settings, "summary"), Verbosity = Text(thread.Settings, "verbosity"), ServiceTier = Text(thread.Settings, "serviceTier"),
                Identity = new(Text(thread.Settings, "nativeSessionId") ?? id.ToString(), id.ToString(), turnId.ToString())
                { RootTurnId = (OptionalId(p, "rootTurnId") ?? turnId).ToString(), ParentTurnId = OptionalId(p, "parentTurnId")?.ToString() },
                OutputSchema = p.TryGetProperty("outputSchema", out var schema) && schema.ValueKind != JsonValueKind.Null ? schema.Clone() : null
            }, p);
            if (Text(p, "serviceTierForTurn") is { } tier)
                options = Apply(options, Element(new JsonObject { ["serviceTier"] = tier }));
            validateOptions?.Invoke(options);
            var defaultsForFutureTurns = Object(effectiveSettings);
            foreach (var field in new[] { "model", "effort", "summary", "personality" })
                if (Text(p, field) is { } sticky) defaultsForFutureTurns[field] = sticky;
            if (p.TryGetProperty("serviceTier", out var persistentTier))
                defaultsForFutureTurns["serviceTier"] = JsonNode.Parse(persistentTier.GetRawText());
            RecordTurnConfiguration(defaultsForFutureTurns, p);
            ValidateConfigurationRequirements(Element(defaultsForFutureTurns),
                thread.Settings.TryGetProperty("nativeConfigurationRequirements", out var requirements) ? requirements : null);
            if (!JsonElement.DeepEquals(Element(defaultsForFutureTurns), thread.Settings))
                thread = await store.UpdateSettingsAsync(thread.Id, thread.Revision, Element(defaultsForFutureTurns), ct).ConfigureAwait(false);
            current = new(turnId, options)
            { ConfigurationRequirements = thread.Settings.TryGetProperty("nativeConfigurationRequirements", out var captured) ? captured.Clone() : null };
            current.Control.InitializeInput(input, clientId, trigger);
            if (!active.TryAdd(id, current)) throw new CodexThreadConflictException();
            current.Completion = RunAsync(thread, current, input);
        }
        finally { admission.Release(); }
        var started = await current.Started.Task.WaitAsync(ct).ConfigureAwait(false);
        return Element(new JsonObject { ["turn"] = Turn(started.Turns.Single(t => t.Id == current.Id), "notLoaded") });
    }

    private async Task RunAsync(CodexThreadDocument thread, Running running, IReadOnlyList<JsonElement> input)
    {
        CodexTurnEvents? events = null;
        try
        {
            events = new CodexTurnEvents(thread.Id.ToString(), running.Id.ToString(), PublishAsync);
            var run = new CodexThreadRun(thread, running.Id, "native-rpc:" + Guid.CreateVersion7(), running.Options, input, running.Control,
                (_, ct) => PublishItemsAsync(thread.Id, running, ct))
            {
                ObserveProvider = events.ProviderAsync,
                ExecutePlan = events.PlanAsync,
                AppliedPatch = events.AppliedPatchAsync
            };
            var result = await (running.ExecuteOwned ?? execute)(run, async started =>
            {
                await EmitAsync("turn/started", new JsonObject { ["threadId"] = thread.Id.ToString(), ["turn"] = Turn(started.Turns.Single(t => t.Id == running.Id), "notLoaded") }).ConfigureAwait(false);
                running.Started.TrySetResult(started);
                await PublishItemsAsync(thread.Id, running, CancellationToken.None).ConfigureAwait(false);
            }, running.Control.Token).ConfigureAwait(false);
            await PublishItemsAsync(thread.Id, running, CancellationToken.None).ConfigureAwait(false);
            await CompletedAsync(result, running).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            running.Failure = error;
            running.Started.TrySetException(error);
            // A failed/unknown journal never authorizes automatic replay or a fake terminal event.
            try
            {
                var actual = await store.ReadAsync(thread.Id, CancellationToken.None).ConfigureAwait(false);
                if (actual.Turns.SingleOrDefault(t => t.Id == running.Id) is { Status: not "inProgress" })
                    await CompletedAsync(actual, running).ConfigureAwait(false);
            }
            catch (Exception readError) when (readError is not OutOfMemoryException) { Interlocked.Increment(ref notificationFailures); }
        }
        finally
        {
            if (events is not null) Interlocked.Add(ref notificationFailures, events.DeliveryFailures);
            running.Control.Seal(); RemoveActive(thread.Id, running); running.Control.Dispose();
        }
    }

    /// <summary>Bind a real host worker to this catalog and control plane. The caller supplies
    /// its existing fenced executor; reads and reconnect never replay it.</summary>
    public async Task<CodexThreadDocument> RunOwnedAsync(CodexThreadDocument thread, Guid turnId,
        CodexResponseOptions options, IReadOnlyList<JsonElement> input,
        Func<CodexThreadRun, Func<CodexThreadDocument, Task>, CancellationToken, Task<CodexThreadDocument>> executeOwned,
        CancellationToken cancellationToken = default)
    {
        Running running;
        await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping) || active.Count >= maximumConcurrentTurns)
                throw new CodexThreadRpcException(-32010, "Native thread host is draining or at capacity.");
            var actual = await store.ReadAsync(thread.Id, cancellationToken).ConfigureAwait(false);
            if (actual.Revision != thread.Revision) throw new CodexThreadConflictException();
            var constraints = actual.Settings.TryGetProperty("nativeConfigurationRequirements", out var required) ? required.Clone() : (JsonElement?)null;
            ValidateConfigurationOptions(options, constraints);
            running = new(turnId, options) { ExecuteOwned = executeOwned, ConfigurationRequirements = constraints };
            if (!active.TryAdd(thread.Id, running)) throw new CodexThreadConflictException();
            loaded.TryAdd(thread.Id, 0);
            running.Completion = RunAsync(thread, running, input);
        }
        finally { admission.Release(); }
        using var registration = cancellationToken.Register(running.Control.Interrupt);
        await running.Completion.ConfigureAwait(false);
        if (running.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return await store.ReadAsync(thread.Id, CancellationToken.None).ConfigureAwait(false);
    }

    private Task CompletedAsync(CodexThreadDocument thread, Running running)
    {
        running.Control.Seal();
        RemoveActive(thread.Id, running);
        var completed = thread.Turns.Single(t => t.Id == running.Id);
        JsonObject snapshot;
        try { snapshot = Turn(completed, "summary"); }
        catch (CodexThreadRpcException) { Interlocked.Increment(ref notificationFailures); snapshot = Turn(completed, "notLoaded"); }
        return EmitAsync("turn/completed", new JsonObject { ["threadId"] = thread.Id.ToString(), ["turn"] = snapshot });
    }
    private void RemoveActive(Guid id, Running running) =>
        ((ICollection<KeyValuePair<Guid, Running>>)active).Remove(new(id, running));

    private JsonElement Steer(JsonElement p)
    {
        ValidateFields(p, ["threadId", "expectedTurnId", "input", "clientUserMessageId"]);
        var clientId = InputIdentity(p, "clientUserMessageId");
        var id = Id(p, "threadId"); var turn = Id(p, "expectedTurnId");
        if (!active.TryGetValue(id, out var running) || running.Id != turn || !running.Started.Task.IsCompletedSuccessfully)
            throw Invalid("Expected active native turn is unavailable.");
        var input = Input(p); validateInput?.Invoke(input); running.Control.Steer(input, clientId);
        return Element(new JsonObject { ["turnId"] = turn.ToString() });
    }

    private async Task<JsonElement> InterruptAsync(JsonElement p, CancellationToken ct)
    {
        ValidateFields(p, ["threadId", "turnId"]);
        var id = Id(p, "threadId"); var turn = Text(p, "turnId") ?? throw Invalid("turnId is required.");
        if (!active.TryGetValue(id, out var running)) throw Invalid("No active native turn.");
        if (turn.Length != 0 && (!Guid.TryParse(turn, out var parsed) || running.Id != parsed)) throw Invalid("Active turn identity changed.");
        running.Control.Interrupt();
        if (turn.Length != 0)
        {
            await running.Completion.WaitAsync(ct).ConfigureAwait(false);
            var journal = await store.ReadAsync(id, ct).ConfigureAwait(false);
            if (journal.Turns.SingleOrDefault(t => t.Id == running.Id) is not { Status: not "inProgress" })
                throw new CodexThreadRpcException(-32010, "Terminal native turn outcome is unconfirmed; no replay authorized.");
        }
        return Json("{}");
    }

    private JsonElement Update(JsonElement p)
    {
        ValidateFields(p, ["threadId", "turnId", "model", "effort", "summary", "serviceTier", "approvalsReviewer"]);
        var id = Id(p, "threadId"); var turn = Id(p, "turnId");
        // Validate even when the target has just become unavailable.
        if (Text(p, "approvalsReviewer") is { } reviewer && reviewer != "user") throw Invalid("Unsupported approval reviewer.");
        _ = Apply(new("validation", "validation"), p);
        var applied = active.TryGetValue(id, out var running) && running.Id == turn
            && running.Control.Update(options =>
            {
                var next = Apply(options, p);
                ValidateConfigurationOptions(next, running.ConfigurationRequirements);
                validateOptions?.Invoke(next); return next;
            }, running.Options);
        return Element(new JsonObject { ["status"] = applied ? "applied" : "targetUnavailable" });
    }

    private static CodexResponseOptions Apply(CodexResponseOptions options, JsonElement p)
    {
        var model = Text(p, "model"); var effort = Text(p, "effort"); var summary = Text(p, "summary");
        if (model is not null && (string.IsNullOrWhiteSpace(model) || model.Length > 100 || model.Any(char.IsControl))) throw Invalid("Invalid model.");
        if (effort is not null && (string.IsNullOrWhiteSpace(effort) || effort.Length > 64 || effort.Any(char.IsControl)))
            throw Invalid("Reasoning effort must be a bounded nonempty advertised value.");
        if (summary is not null && summary is not ("auto" or "concise" or "detailed" or "none")) throw Invalid("Invalid reasoning summary.");
        var tier = Text(p, "serviceTier");
        if (tier is not null && tier is not ("auto" or "default" or "flex" or "priority")) throw Invalid("Invalid service tier.");
        return options with { Model = model ?? options.Model, ReasoningEffort = effort ?? options.ReasoningEffort,
            ReasoningSummary = summary ?? options.ReasoningSummary,
            ServiceTier = p.TryGetProperty("serviceTier", out _) ? tier : options.ServiceTier };
    }

    private static JsonElement TurnSettings(JsonElement baseline, JsonElement parameters)
    {
        var overrides = new JsonObject();
        foreach (var field in new[] { "cwd", "approvalPolicy", "approvalsReviewer" })
            if (parameters.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
                overrides[field] = JsonNode.Parse(value.GetRawText());
        if (parameters.TryGetProperty("sandboxPolicy", out var policy) && policy.ValueKind != JsonValueKind.Null)
        {
            if (policy.ValueKind != JsonValueKind.Object) throw Invalid("Sandbox policy must be an object.");
            ValidateFields(policy, ["type", "networkAccess"]);
            if (policy.TryGetProperty("networkAccess", out var network) && network.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("Sandbox networkAccess must be a boolean when present.");
            if (Text(policy, "type") != "readOnly" || Flag(policy, "networkAccess"))
                throw Invalid("This host supports only readOnly sandbox with networkAccess false.");
            overrides["sandbox"] = "read-only";
        }
        if (parameters.TryGetProperty("disabledPluginIds", out var disabled) && disabled.ValueKind != JsonValueKind.Null)
        {
            if (disabled.ValueKind != JsonValueKind.Array || disabled.GetArrayLength() > 128 || disabled.EnumerateArray().Any(x =>
                x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()) || x.GetString() is { } id && (id.Length > 512 || id.Any(char.IsControl))))
                throw Invalid("disabledPluginIds must be a bounded string array.");
            // This host advertises no plugin tools; retain the replacement for future captures.
            overrides["disabledPluginIds"] = JsonNode.Parse(disabled.GetRawText());
        }
        // Validate every override before the settings CAS or durable turn admission.
        // Null preserves the prior setting, as in the pinned turn/start contract.
        var disabledPlugins = overrides["disabledPluginIds"]?.DeepClone();
        overrides.Remove("disabledPluginIds");
        var resolved = Object(Resolve(Element(overrides), baseline, ["cwd", "approvalPolicy", "approvalsReviewer", "sandbox"]));
        if (disabledPlugins is not null) resolved["disabledPluginIds"] = disabledPlugins;
        return Element(resolved);
    }

    private static JsonElement[] Input(JsonElement p)
    {
        if (!p.TryGetProperty("input", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 128
            || System.Text.Encoding.UTF8.GetByteCount(items.GetRawText()) > 1_048_576) throw Invalid("Bounded input array is required.");
        if (p.TryGetProperty("toolOutput", out var toolOutput) && toolOutput.ValueKind != JsonValueKind.Null)
            return ToolOutputInput(items, toolOutput);
        var content = new JsonArray();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("Invalid user input.");
            var type = Text(item, "type");
            if (type == "text")
            {
                ValidateFields(item, ["type", "text", "text_elements"]);
                var text = Text(item, "text") ?? throw Invalid("Input text is required.");
                if (item.TryGetProperty("text_elements", out var elements) && elements.ValueKind == JsonValueKind.Array && elements.GetArrayLength() != 0)
                    throw Invalid("Attached text elements require a host attachment resolver.");
                content.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = text });
            }
            else if (type == "image")
            {
                ValidateFields(item, ["type", "url", "detail"]);
                var url = Text(item, "url") ?? throw Invalid("Image URL required.");
                if (!(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") && !url.StartsWith("data:image/", StringComparison.Ordinal))
                    throw Invalid("Explicit https or inline image input required; local paths need host permission resolution.");
                var image = new JsonObject { ["type"] = "input_image", ["image_url"] = url };
                if (Text(item, "detail") is { } detail) image["detail"] = detail is "auto" or "low" or "high" or "original" ? detail : throw Invalid("Invalid image detail.");
                content.Add((JsonNode)image);
            }
            else throw Invalid("This input variant requires an explicit host resolver: " + type);
        }
        return [Element(new JsonObject { ["role"] = "user", ["content"] = content })];
    }

    private async Task PublishItemsAsync(Guid threadId, Running running, CancellationToken ct)
    {
        var thread = await store.ReadAsync(threadId, ct).ConfigureAwait(false);
        var turn = thread.Turns.Single(t => t.Id == running.Id);
        JsonObject[] projected;
        try { projected = Items(turn); }
        catch (CodexThreadRpcException) { Interlocked.Increment(ref notificationFailures); return; }
        foreach (var item in projected)
        {
            var id = item["id"] is JsonValue identity && identity.TryGetValue<string>(out var text)
                ? text : throw Invalid("Item identity is required.");
            if (running.StartedItems.Add(id)) await EmitAsync("item/started", new JsonObject
            { ["threadId"] = threadId.ToString(), ["turnId"] = running.Id.ToString(), ["item"] = item.DeepClone(), ["startedAtMs"] = null }).ConfigureAwait(false);
            if (item["status"]?.GetValue<string>() != "inProgress" && running.CompletedItems.Add(id))
                await EmitAsync("item/completed", new JsonObject { ["threadId"] = threadId.ToString(), ["turnId"] = running.Id.ToString(),
                    ["item"] = item.DeepClone(), ["completedAtMs"] = null }).ConfigureAwait(false);
        }
    }

    /// <summary>Only trusted host producers may publish diff/plan/moderation facts. These are
    /// notifications, never execution authority or a provider billing acknowledgement.</summary>
    public async Task PublishAsync(string method, JsonElement parameters)
    {
        if (method is not ("turn/diff/updated" or "turn/plan/updated" or "turn/moderationMetadata")) throw Invalid("Unsupported host notification.");
        var id = Id(parameters, "threadId"); var turn = Id(parameters, "turnId");
        ValidateFields(parameters, method switch
        {
            "turn/diff/updated" => ["threadId", "turnId", "diff"],
            "turn/plan/updated" => ["threadId", "turnId", "explanation", "plan"],
            _ => ["threadId", "turnId", "metadata"]
        });
        if (System.Text.Encoding.UTF8.GetByteCount(parameters.GetRawText()) > 1_048_576) throw Invalid("Notification exceeds its bound.");
        if (method == "turn/diff/updated" && Text(parameters, "diff") is null) throw Invalid("Diff text required.");
        if (method == "turn/plan/updated" && (!parameters.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Array)) throw Invalid("Plan array required.");
        if (method == "turn/moderationMetadata" && !parameters.TryGetProperty("metadata", out _)) throw Invalid("Moderation metadata required.");
        if (!active.TryGetValue(id, out var running) || running.Id != turn || !running.Started.Task.IsCompletedSuccessfully)
            throw Invalid("Notification target is not active.");
        await EmitAsync(method, Object(parameters)).ConfigureAwait(false);
    }

    private async Task EmitAsync(string method, JsonObject parameters)
    {
        try { await notify(method, Element(parameters)).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { Interlocked.Increment(ref notificationFailures); }
    }
}
