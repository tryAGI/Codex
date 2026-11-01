using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

public sealed record CodexFunctionCall(string Id, string Name, string ArgumentsJson)
{
    public string? Namespace { get; init; }
}
public sealed record CodexAgentResult(string Text, IReadOnlyList<JsonElement> History, int Requests, int ToolCalls)
{
    public bool CompletedDirectly { get; init; }
    public IReadOnlyList<string> ExecutedCallIds { get; init; } = [];
}

/// <summary>Bounded native reasoning/tool loop. The host retains responsibility for tool authorization and durable action deduplication.</summary>
public sealed class CodexAgent(CodexClient client)
{
    public async Task<CodexAgentResult> RunAsync(CodexResponseOptions options, IReadOnlyList<JsonElement> history,
        Func<CodexFunctionCall, CancellationToken, Task<string>> execute,
        int maximumRequests = 8, Action<CodexResponseEvent>? observe = null, CancellationToken cancellationToken = default,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null,
        Func<CodexFunctionCall, string, string?>? completeAfterSingleTool = null,
        IReadOnlyCollection<string>? previouslyExecutedCallIds = null)
        => await RunWithRichResultsAsync(options, history, async (call, ct) =>
            new CodexToolOutput(await execute(call, ct).ConfigureAwait(false)), maximumRequests, observe,
            cancellationToken, observeDiagnostics, completeAfterSingleTool,
            previouslyExecutedCallIds: previouslyExecutedCallIds).ConfigureAwait(false);

    /// <summary>Rich results preserve pixels. Parallel execution additionally requires explicit host classification;
    /// unclassified calls remain serial barriers, regardless of the model's parallel_tool_calls setting.</summary>
    public async Task<CodexAgentResult> RunWithRichResultsAsync(CodexResponseOptions options, IReadOnlyList<JsonElement> history,
        Func<CodexFunctionCall, CancellationToken, Task<CodexToolOutput>> execute,
        int maximumRequests = 8, Action<CodexResponseEvent>? observe = null, CancellationToken cancellationToken = default,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null,
        Func<CodexFunctionCall, string, string?>? completeAfterSingleTool = null,
        Func<CodexFunctionCall, bool>? canExecuteConcurrently = null,
        IReadOnlyCollection<string>? previouslyExecutedCallIds = null,
        bool allowSilentCompletion = false,
        Func<CodexResponseEvent, CancellationToken, Task>? observeAsync = null,
        Func<JsonElement, CancellationToken, Task>? observeValidatedItem = null)
        => await RunToolsAsync(options, history, (call, ct) => execute(call.Function(), ct), maximumRequests,
            observe, cancellationToken, observeDiagnostics,
            completeAfterSingleTool is null ? null : (call, text) => completeAfterSingleTool(call.Function(), text),
            canExecuteConcurrently is null ? null : call => canExecuteConcurrently(call.Function()),
            previouslyExecutedCallIds, allowCustom: false, allowSilentCompletion, observeAsync, observeValidatedItem).ConfigureAwait(false);

    /// <summary>Executes advertised function and custom tools with explicit type/namespace identity.
    /// Freeform input remains data; the caller supplies authorization and a durable dispatch ledger.</summary>
    public Task<CodexAgentResult> RunWithToolsAsync(CodexResponseOptions options, IReadOnlyList<JsonElement> history,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute,
        int maximumRequests = 8, Action<CodexResponseEvent>? observe = null, CancellationToken cancellationToken = default,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null,
        Func<CodexToolCall, string, string?>? completeAfterSingleTool = null,
        Func<CodexToolCall, bool>? canExecuteConcurrently = null,
        IReadOnlyCollection<string>? previouslyExecutedCallIds = null,
        bool allowSilentCompletion = false,
        Func<CodexResponseEvent, CancellationToken, Task>? observeAsync = null)
        => RunToolsAsync(options, history, execute, maximumRequests, observe, cancellationToken, observeDiagnostics,
            completeAfterSingleTool, canExecuteConcurrently, previouslyExecutedCallIds, allowCustom: true, allowSilentCompletion,
            observeAsync: observeAsync);

    /// <summary>Persists reconciled output items before tools execute. This overload preserves the
    /// original API and also supports custom tools; streamed previews are never passed as validated items.</summary>
    public Task<CodexAgentResult> RunWithToolsAsync(CodexResponseOptions options, IReadOnlyList<JsonElement> history,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute,
        Func<JsonElement, CancellationToken, Task> observeValidatedItem,
        int maximumRequests = 8, Action<CodexResponseEvent>? observe = null, CancellationToken cancellationToken = default,
        Action<CodexResponseDiagnostic>? observeDiagnostics = null,
        Func<CodexToolCall, string, string?>? completeAfterSingleTool = null,
        Func<CodexToolCall, bool>? canExecuteConcurrently = null,
        IReadOnlyCollection<string>? previouslyExecutedCallIds = null, bool allowSilentCompletion = false,
        CodexAgentControl? control = null,
        Func<CodexResponseEvent, CancellationToken, Task>? observeAsync = null)
        => RunToolsAsync(options, history, execute, maximumRequests, observe, cancellationToken, observeDiagnostics,
            completeAfterSingleTool, canExecuteConcurrently, previouslyExecutedCallIds, allowCustom: true,
            allowSilentCompletion, observeAsync: observeAsync, observeValidatedItem: observeValidatedItem, control: control);

    private async Task<CodexAgentResult> RunToolsAsync(CodexResponseOptions options, IReadOnlyList<JsonElement> history,
        Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> execute, int maximumRequests,
        Action<CodexResponseEvent>? observe, CancellationToken cancellationToken,
        Action<CodexResponseDiagnostic>? observeDiagnostics,
        Func<CodexToolCall, string, string?>? completeAfterSingleTool,
        Func<CodexToolCall, bool>? canExecuteConcurrently,
        IReadOnlyCollection<string>? previouslyExecutedCallIds, bool allowCustom, bool allowSilentCompletion = false,
        Func<CodexResponseEvent, CancellationToken, Task>? observeAsync = null,
        Func<JsonElement, CancellationToken, Task>? observeValidatedItem = null,
        CodexAgentControl? control = null)
    {
        if (maximumRequests is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        if (options.MaximumConcurrentTools is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(options.MaximumConcurrentTools));
        var input = history.Select(x => x.Clone()).ToList();
        var seen = ExecutedIds(history, previouslyExecutedCallIds);
        var calls = 0;
        var advertised = CodexToolCalls.Advertised(options.Tools, allowCustom);
        for (var request = 1; request <= maximumRequests; request++)
        {
            options = control?.Capture(options, input) ?? options;
            var requestOptions = request == 1 ? options : options with { RequiredFirstFunction = null };
            JsonElement? result = null;
            CodexResponseDiagnostic? requestDiagnostic = null;
            var completedItems = new SortedDictionary<int, JsonElement>();
            await foreach (var item in client.StreamResponseAsync(requestOptions, input, cancellationToken,
                observeDiagnostics is null ? null : point =>
                {
                    requestDiagnostic = point with { RequestNumber = request };
                    observeDiagnostics(requestDiagnostic);
                }).ConfigureAwait(false))
            {
                observe?.Invoke(item);
                if (observeAsync is not null) await observeAsync(item, cancellationToken).ConfigureAwait(false);
                if (item.Type == "response.output_item.done")
                {
                    if (!item.Payload.TryGetProperty("output_index", out var indexValue) || !indexValue.TryGetInt32(out var index) ||
                        index is < 0 or >= 4096 || !item.Payload.TryGetProperty("item", out var completedItem) ||
                        completedItem.ValueKind != JsonValueKind.Object)
                        throw new CodexProtocolException("Completed output item has an invalid index or body.");
                    if (completedItems.TryGetValue(index, out var previous) && !JsonElement.DeepEquals(previous, completedItem))
                        throw new CodexProtocolException("Conflicting completed output items.");
                    completedItems[index] = completedItem.Clone();
                }
                if (item.Type == "response.completed") result = item.Payload.GetProperty("response").Clone();
            }
            if (result is null || !result.Value.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                throw new CodexProtocolException("Completed response has no output array.");
            var items = output.EnumerateArray().Select(x => x.Clone()).ToArray();
            if (items.Length == 0)
            {
                if (!completedItems.Keys.SequenceEqual(Enumerable.Range(0, completedItems.Count)))
                    throw new CodexProtocolException("Completed response has missing output items.");
                items = completedItems.Values.ToArray();
            }
            else if (completedItems.Any(x => x.Key >= items.Length || !JsonElement.DeepEquals(x.Value, items[x.Key])))
                throw new CodexProtocolException("Terminal response conflicts with completed output items.");
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
                if (CodexRealtimeProtocol.Text(item, "id") is { } id && !itemIds.Add(id))
                    throw new CodexProtocolException("Repeated output item ID.");
            var functions = items.Where(x => CodexRealtimeProtocol.Text(x, "type") is "function_call" or "custom_tool_call").ToArray();
            if (requestOptions.RequiredFirstFunction is { } required &&
                (functions.Length != 1 || CodexRealtimeProtocol.Text(functions[0], "type") != "function_call" ||
                 CodexRealtimeProtocol.Text(functions[0], "name") != required ||
                 CodexRealtimeProtocol.Text(functions[0], "namespace") is not null ||
                 items.Any(item => CodexRealtimeProtocol.Text(item, "type") == "message")))
                throw new CodexProtocolException("Response omitted the exclusive required first function; no action was executed.");
            // Streamed previews are observations. Completion is released only after the whole
            // terminal output reconciles with streamed items; a conflict never completes a preview.
            if (observeValidatedItem is not null)
                foreach (var item in items) await observeValidatedItem(item, cancellationToken).ConfigureAwait(false);
            // Preserve reasoning encrypted_content and complete message/tool items for the next request.
            input.AddRange(items);
            if (functions.Length > 16) throw new CodexProtocolException("Response exceeded the tool call limit.");
            if (functions.Length == 0)
            {
                if (control is not null && !control.TryFinish())
                {
                    if (request == maximumRequests) throw new CodexAgentRequestLimitException(maximumRequests, calls);
                    continue;
                }
                var text = new List<string>();
                foreach (var item in items.Where(x => CodexRealtimeProtocol.Text(x, "type") == "message"))
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        text.AddRange(content.EnumerateArray().Where(x => CodexRealtimeProtocol.Text(x, "type") == "output_text").Select(x => CodexRealtimeProtocol.Text(x, "text") ?? ""));
                var answer = string.Concat(text);
                // A live host may accept model-decided silence after a validated terminal response.
                // Stream, item and tool validation above still applies; no incomplete reply is accepted.
                if (string.IsNullOrWhiteSpace(answer) && !allowSilentCompletion)
                    throw new CodexProtocolException("Completed agent response has no text or tool calls.");
                return new(answer, input, request, calls) { ExecutedCallIds = seen.Order(StringComparer.Ordinal).ToArray() };
            }
            if (request == maximumRequests)
                throw new CodexAgentRequestLimitException(maximumRequests, calls);
            var validated = new List<CodexToolCall>(functions.Length);
            foreach (var function in functions)
            {
                var call = CodexToolCalls.Validate(function, advertised, allowCustom);
                if (!seen.Add(call.Id)) throw new CodexProtocolException("Repeated tool call ID; refusing to replay an action.");
                if (seen.Count > 65536) throw new CodexProtocolException("Executed call IDs exceeded the bound.");
                validated.Add(call);
            }
            var outputs = new CodexToolOutput[validated.Count];
            // Classify the entire batch before execution. A model cannot authorize concurrent effects.
            var parallel = validated.Select(call => options.ParallelToolCalls && canExecuteConcurrently?.Invoke(call) == true).ToArray();
            async Task ExecuteAt(int index)
            {
                var call = validated[index];
                cancellationToken.ThrowIfCancellationRequested();
                using var activity = CodexClient.ActivitySource.StartActivity("codex.tool.execute");
                activity?.SetTag("tool.name", call.Name); activity?.SetTag("tool.call_id", call.Id);
                activity?.SetTag("tool.kind", call.Kind.ToString()); activity?.SetTag("tool.namespace", call.Namespace);
                var toolStarted = Stopwatch.GetTimestamp();
                void MarkTool(string phase, bool terminal)
                {
                    if (requestDiagnostic is null) return;
                    (observeDiagnostics ?? throw new InvalidDataException("Required data was null."))(requestDiagnostic with
                    {
                        Phase = phase,
                        ElapsedMilliseconds = Stopwatch.GetElapsedTime(requestDiagnostic.StartedTimestamp).TotalMilliseconds,
                        FunctionId = SafeMetadata(call.Id), ToolName = SafeMetadata(call.Name),
                        ToolNamespace = SafeMetadata(call.Namespace),
                        OutputKind = call.Kind == CodexToolCallKind.Custom ? "custom_tool_call" : "function_call",
                        OutputIndex = null, ArgumentCharacters = call.Input.Length, ParallelDispatch = parallel[index],
                        ToolDurationMilliseconds = terminal ? Stopwatch.GetElapsedTime(toolStarted).TotalMilliseconds : null,
                        // Usage belongs exclusively to response.completed, never to copied tool events.
                        InputTokens = null, CachedInputTokens = null, OutputTokens = null, ReasoningTokens = null,
                    });
                }
                MarkTool("tool.started", false);
                try { outputs[index] = await execute(call, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { MarkTool("tool.cancelled", true); throw; }
                catch { MarkTool("tool.failed", true); throw; }
                MarkTool("tool.completed", true);
            }
            for (var index = 0; index < validated.Count;)
            {
                if (!parallel[index]) { await ExecuteAt(index++).ConfigureAwait(false); continue; }
                var pending = new List<Task>();
                do { pending.Add(ExecuteAt(index++)); }
                while (index < validated.Count && parallel[index] && pending.Count < options.MaximumConcurrentTools);
                // Await all in-flight calls even on failure. Never retry a partially completed batch.
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            for (var index = 0; index < validated.Count; index++)
            {
                using var toolOutput = JsonDocument.Parse(new JsonObject { ["type"] = validated[index].Kind == CodexToolCallKind.Custom ? "custom_tool_call_output" : "function_call_output",
                    ["call_id"] = validated[index].Id, ["output"] = outputs[index].Content() }.ToJsonString());
                input.Add(toolOutput.RootElement.Clone());
            }
            calls += validated.Count;
            // A host may finish an explicitly terminal, known result. Never cut a batch short,
            // skip terminal response validation, or swallow accompanying model messages.
            cancellationToken.ThrowIfCancellationRequested();
            if (validated.Count == 1 && outputs[0].Images.Count == 0 && !items.Any(x => CodexRealtimeProtocol.Text(x, "type") == "message") &&
                completeAfterSingleTool?.Invoke(validated[0], outputs[0].Text) is { Length: <= 4096 } direct &&
                (direct.Length == 0 ? allowSilentCompletion : !string.IsNullOrWhiteSpace(direct)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (direct.Length > 0)
                {
                    using var answer = JsonDocument.Parse(new JsonObject { ["role"] = "assistant", ["content"] = direct }.ToJsonString());
                    input.Add(answer.RootElement.Clone());
                }
                return new(direct, input, request, calls) { CompletedDirectly = true, ExecutedCallIds = seen.Order(StringComparer.Ordinal).ToArray() };
            }
        }
        throw new CodexProtocolException("Native agent reached its bounded request limit.");
    }

    private static string? SafeMetadata(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) ? value : null;

    internal static HashSet<string> ExecutedIds(IReadOnlyList<JsonElement> history, IReadOnlyCollection<string>? previous)
    {
        if (previous is null && history.Any(item => CodexRealtimeProtocol.Text(item, "type") is "compaction" or "compaction_summary"))
            throw new ArgumentException("Compacted history requires the host's separately preserved executed call IDs, including an explicit empty set when no calls occurred.", nameof(previous));
        if (previous is { Count: > 65536 } || previous?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Executed call IDs must be nonempty and bounded.", nameof(previous));
        var ids = history.Where(item => CodexRealtimeProtocol.Text(item, "type") is "function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output")
            .Select(item => CodexRealtimeProtocol.Text(item, "call_id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (previous is not null) ids.UnionWith(previous);
        if (ids.Count > 65536) throw new ArgumentException("Executed call IDs exceeded the bound.");
        return ids;
    }
}
