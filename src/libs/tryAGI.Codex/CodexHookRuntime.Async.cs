using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial class CodexHookRuntime
{
    private readonly CodexAsyncHookQueue? asyncQueue;

    /// <summary>Enables explicitly owned asynchronous hook scheduling in addition to the
    /// synchronous lifecycle. The queue is owned and disposed by the embedding host.</summary>
    public CodexHookRuntime(Func<CodexHookInvocation, CancellationToken, ValueTask<bool>> authorize,
        Func<CodexHookInvocation, CancellationToken, Task<CodexHookExecutionResult>> execute,
        Func<string, JsonElement, CancellationToken, Task> notify, TimeProvider? clock,
        CodexAsyncHookQueue asyncQueue) : this(authorize, execute, notify, clock)
    {
        this.asyncQueue = asyncQueue ?? throw new ArgumentNullException(nameof(asyncQueue));
    }

    private async Task<CodexAsyncHookCompletion> ExecuteAsyncHook(CodexHookInvocation invocation,
        CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var entries = new List<CodexHookOutputEntry>();
        var contexts = new List<string>();
        var warnings = new List<string>();
        var status = "completed";
        string? message = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (invocation.Definition.TrustStatus is "untrusted" or "modified" ||
                !await authorize(invocation, ct).AsTask().WaitAsync(ct).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Async hook was not admitted.");
            ct.ThrowIfCancellationRequested();
            var result = await execute(invocation, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) throw new IOException("Async hook handler failed.");
            if (result.Output is { } output)
            {
                var parsed = ParseOutput(invocation.Context.EventName, output);
                if (parsed.SystemMessage is { } warning)
                {
                    warnings.Add(warning);
                    entries.Add(new("warning", warning));
                }
                if (parsed.Context is { } context)
                {
                    contexts.Add(context);
                    entries.Add(new("context", context));
                }
                // Async hooks have no control effects, regardless of their declared event.
                if (parsed.Block || parsed.Stop || parsed.Permission is not null ||
                    parsed.Input is not null || parsed.McpOutput is not null)
                {
                    const string ignored = "Asynchronous hook control effects were ignored.";
                    warnings.Add(ignored);
                    entries.Add(new("warning", ignored));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "stopped";
            message = "Asynchronous hook was cancelled or expired.";
            entries.Clear();
            contexts.Clear();
            warnings.Clear();
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            status = "failed";
            message = "Asynchronous hook execution or output was not accepted.";
            entries.Clear();
            contexts.Clear();
            warnings.Clear();
            entries.Add(new("error", message));
        }

        return new(invocation.Context, Summary(invocation, status, message, started, clock.GetUtcNow(), entries),
            contexts.AsReadOnly(), warnings.AsReadOnly());
    }

    private void ScheduleAsyncHooks(CodexHookContext context, CodexHookDefinition[] selected,
        JsonElement original, List<JsonElement> runs, CancellationToken ct)
    {
        if (asyncQueue is null) return;
        foreach (var definition in selected.Where(definition => definition.ExecutionMode == "async"))
        {
            ct.ThrowIfCancellationRequested();
            var invocation = new CodexHookInvocation(context, definition, $"{context.EventId}:{definition.Id}", original);
            var result = asyncQueue.Schedule(invocation, ExecuteAsyncHook);
            if (result == CodexAsyncHookSchedule.CapacityExceeded)
            {
                // Capacity failure remains visible without giving async work control over the synchronous gate.
                var now = clock.GetUtcNow();
                const string message = "Asynchronous hook capacity exhausted.";
                runs.Add(Summary(invocation, "failed", message, now, now, [new("error", message)]));
            }
        }
    }
}
