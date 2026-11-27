using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>A host-selected mapping from a registered model tool to one exact MCP server
/// registration generation and remote tool. Discovery and model text cannot choose the scope.</summary>
public sealed record CodexNativeMcpToolBinding(CodexToolIdentity Identity,
    CodexMcpConnectionScope Scope, long Revision, string RemoteToolName);

/// <summary>The embedding host's authorized model output and public MCP item result.
/// The public result must have canonical MCP content; private metadata is never copied implicitly.</summary>
public sealed record CodexNativeMcpToolProjection(CodexToolOutput ModelOutput, JsonElement PublicResult);

/// <summary>Runs the real MCP client through a host-selected scope and an already admitted
/// native reasoning tool callback. The MCP client's existing per-request authorization
/// still governs the actual operation. No implicit retry or server selection is performed.</summary>
public sealed class CodexNativeMcpToolExecutor
{
    private readonly CodexMcpConnectionPool pool;
    private readonly CodexNativeMcpToolBinding binding;
    private readonly Func<JsonElement, CancellationToken, ValueTask<CodexNativeMcpToolProjection>> project;

    /// <summary>Create an adapter for one trusted registration, with an explicit public-result
    /// projection. Hosts may route their existing native tool callback to ExecuteAsync.</summary>
    public CodexNativeMcpToolExecutor(CodexMcpConnectionPool pool, CodexNativeMcpToolBinding binding,
        Func<JsonElement, CancellationToken, ValueTask<CodexNativeMcpToolProjection>> project)
    {
        this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(binding.Identity);
        ArgumentNullException.ThrowIfNull(binding.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.RemoteToolName);
        if (binding.Revision < 1) throw new ArgumentOutOfRangeException(nameof(binding));
    }

    /// <summary>Invoke exactly one authorized MCP tools/call. The observed start is committed
    /// before dispatch; a terminal result or actual JSON-RPC error completes the canonical
    /// backing item. A cancelled or ambiguous transport remains unresolved, never replayed.</summary>
    public async Task<CodexToolOutput> ExecuteAsync(CodexToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var context = call.NativeArtifacts ?? throw new InvalidOperationException("MCP native execution requires a journal-admitted tool scope.");
        if (call.Kind != CodexToolCallKind.Function ||
            new CodexToolIdentity(call.Namespace, call.Name) != binding.Identity ||
            context.CallId != call.Id || context.AccountId != binding.Scope.AccountId ||
            binding.Scope.ThreadId is { } thread && thread != context.ThreadId.ToString())
            throw new UnauthorizedAccessException("MCP binding does not match the admitted tool and account/thread scope.");
        using var parsed = JsonDocument.Parse(call.Input);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("MCP function arguments must be an object.", nameof(call));

        JsonObject Item(string status) => new()
        {
            ["type"] = "mcpToolCall", ["id"] = context.ItemId,
            ["server"] = binding.Scope.ServerName, ["tool"] = binding.RemoteToolName,
            ["status"] = status, ["arguments"] = JsonNode.Parse(parsed.RootElement.GetRawText()),
        };

        // An already admitted tool callback can still be invoked incorrectly by its host.
        // Claim this adapter invocation once, including ambiguous failures, before opening.
        await context.ClaimMcpInvocationAsync(cancellationToken).ConfigureAwait(false);
        var client = await pool.OpenAsync(binding.Scope, binding.Revision, cancellationToken).ConfigureAwait(false);
        await context.ObserveAsync(CodexNativeArtifactStage.Started, Element(Item("inProgress")), cancellationToken).ConfigureAwait(false);
        JsonElement result;
        try
        {
            result = await client.CallToolAsync(binding.RemoteToolName, parsed.RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexMcpRpcException error)
        {
            // This is an observed terminal RPC error, not a guessed transport outcome.
            var failed = Item("failed");
            failed["error"] = new JsonObject { ["message"] = "MCP JSON-RPC error " + error.Code };
            await context.ObserveAsync(CodexNativeArtifactStage.Completed, Element(failed), cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            throw new CodexProtocolException("MCP tool returned no valid terminal content.");
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new CodexProtocolException("MCP tool returned no valid terminal content.");

        var projected = await project(result, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(projected);
        ArgumentNullException.ThrowIfNull(projected.ModelOutput);
        if (projected.PublicResult.ValueKind != JsonValueKind.Object ||
            !projected.PublicResult.TryGetProperty("content", out var publicContent) || publicContent.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("A canonical host-authorized MCP result is required.");
        var terminal = Item(isError.ValueKind == JsonValueKind.True ? "failed" : "completed");
        terminal["result"] = JsonNode.Parse(projected.PublicResult.GetRawText());
        await context.ObserveAsync(CodexNativeArtifactStage.Completed, Element(terminal), cancellationToken).ConfigureAwait(false);
        return projected.ModelOutput;
    }

    private static JsonElement Element(JsonObject item)
    {
        using var document = JsonDocument.Parse(item.ToJsonString());
        return document.RootElement.Clone();
    }
}
