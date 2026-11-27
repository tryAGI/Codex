using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>A reasoning effort advertised by the model provider, including new effort names.</summary>
public sealed record CodexModelReasoningEffort(string Effort, string? Description);

/// <summary>Provider-observed model metadata, not a guarantee of account entitlement.</summary>
public sealed record CodexModelCatalogEntry(
    string Model, string? DisplayName, string? Description, string? Visibility,
    string? DefaultReasoningEffort, IReadOnlyList<CodexModelReasoningEffort>? SupportedReasoningEfforts,
    IReadOnlyList<string>? InputModalities, bool? SupportedInApi, long? ContextWindow,
    long? MaximumContextWindow, bool? SupportsVerbosity, bool? SupportsReasoningSummary,
    JsonElement Raw)
{
    /// <summary>True/false only when the provider advertised its effort list; otherwise unknown.</summary>
    public bool? SupportsReasoningEffort(string effort) =>
        SupportedReasoningEfforts?.Any(value => string.Equals(value.Effort, effort, StringComparison.Ordinal));
}

/// <summary>A bounded model catalog observed from the explicitly selected ChatGPT provider.</summary>
public sealed record CodexModelCatalogSnapshot(
    string ProviderId, string ClientVersion, DateTimeOffset ObservedAt, string? ETag,
    IReadOnlyList<CodexModelCatalogEntry> Models)
{
    /// <summary>Find an exact advertised slug without guessing another model.</summary>
    public CodexModelCatalogEntry? Find(string model) =>
        Models.FirstOrDefault(entry => string.Equals(entry.Model, model, StringComparison.Ordinal));
}

public sealed partial class CodexClient
{
    private readonly object modelCatalogGate = new();
    private ModelCatalogCache? modelCatalogCache;
    private long modelCatalogGeneration;

    private sealed record ModelCatalogIdentity(string Account, string User, bool Restricted, string ClientVersion);
    private sealed record ModelCatalogCache(ModelCatalogIdentity Identity, CodexModelCatalogSnapshot Snapshot);
    private static readonly TimeSpan ModelCatalogTtl = TimeSpan.FromMinutes(5);

    /// <summary>Discovers ChatGPT Codex models through the native models endpoint.
    /// Never switches to API credentials or a different provider. Cache entries are isolated by
    /// account, known user and client version; failed refreshes do not imply current entitlements.</summary>
    /// <param name="clientVersion">Codex client version sent to the provider.</param>
    /// <param name="forceRefresh">Revalidate even if the current snapshot is still fresh.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    public async Task<CodexModelCatalogSnapshot> GetModelCatalogAsync(string clientVersion,
        bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var backend = ValidateModelCatalogEndpoint(clientVersion);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        var ct = deadline.Token;
        var auth = await credentials.GetCredentialsAsync(ct).ConfigureAwait(false);
        var identity = string.IsNullOrWhiteSpace(auth.ChatGptUserId) ? null :
            new ModelCatalogIdentity(auth.AccountId, auth.ChatGptUserId, auth.IsFedRampAccount, clientVersion);
        ModelCatalogCache? cached;
        long generation;
        lock (modelCatalogGate)
        {
            if (identity is null || modelCatalogCache?.Identity != identity) modelCatalogCache = null;
            cached = modelCatalogCache;
            if (!forceRefresh && cached is not null && DateTimeOffset.UtcNow - cached.Snapshot.ObservedAt < ModelCatalogTtl)
                return cached.Snapshot;
            // Once revalidation starts, no subsequent caller may reuse the old entry as fresh.
            modelCatalogCache = null;
            generation = ++modelCatalogGeneration;
        }

        var snapshot = await FetchModelCatalogAsync(backend, auth, clientVersion, cached?.Snapshot,
            TimeProvider.System, ct).ConfigureAwait(false);

        // A refresh may have raced with logout, account switching or restricted-account changes.
        var active = await credentials.GetCredentialsAsync(ct).ConfigureAwait(false);
        if (!string.Equals(auth.AccountId, active.AccountId, StringComparison.Ordinal) ||
            !string.Equals(auth.ChatGptUserId, active.ChatGptUserId, StringComparison.Ordinal) ||
            auth.IsFedRampAccount != active.IsFedRampAccount ||
            identity is null && !string.Equals(auth.AccessToken, active.AccessToken, StringComparison.Ordinal))
            throw new CodexProtocolException("Authorization changed during model discovery.");
        ct.ThrowIfCancellationRequested();
        lock (modelCatalogGate)
        {
            // Do not let a slower request overwrite a newer account/version snapshot.
            if (generation == modelCatalogGeneration && identity is not null)
                modelCatalogCache = new(identity, snapshot);
        }
        return snapshot;
    }

    private Uri ValidateModelCatalogEndpoint(string clientVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        if (clientVersion.Length > 128 || clientVersion.Any(char.IsControl))
            throw new ArgumentException("Invalid catalog client version.", nameof(clientVersion));
        if (realtimeApiKeyProvider is not null) throw new CodexAuthorizationUnavailableException();
        // Catalog metadata is account-specific. Never send an OAuth credential to a custom origin.
        var backend = options.BackendBaseUri;
        if (backend.Scheme != "https" || !backend.IdnHost.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
            !backend.IsDefaultPort || backend.UserInfo.Length != 0 || backend.Query.Length != 0 ||
            backend.Fragment.Length != 0 || backend.AbsolutePath.TrimEnd('/') != "/backend-api/codex")
            throw new ArgumentException("Model discovery requires the trusted ChatGPT Codex backend.");
        return backend;
    }

    private async Task<CodexModelCatalogSnapshot> FetchModelCatalogAsync(Uri backend, CodexCredentials auth,
        string clientVersion, CodexModelCatalogSnapshot? cached, TimeProvider clock, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(backend.AbsoluteUri.TrimEnd('/') + "/models?client_version=" + Uri.EscapeDataString(clientVersion)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
        request.Headers.Add("originator", options.Originator);
        if (cached?.ETag is { } tag && EntityTagHeaderValue.TryParse(tag, out var parsedTag) && parsedTag.Tag != "*")
            request.Headers.IfNoneMatch.Add(parsedTag);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cached is null || request.Headers.IfNoneMatch.Count != 1)
                throw new CodexProtocolException("Model catalog returned an unsolicited not-modified response.");
            return cached with { ObservedAt = clock.GetUtcNow() };
        }
        EnsureSuccess(response);
        try
        {
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, options.MaximumMessageBytes, ct).ConfigureAwait(false));
            return new("openai", clientVersion, clock.GetUtcNow(), response.Headers.ETag?.ToString(),
                ParseModelCatalog(document.RootElement));
        }
        catch (JsonException)
        {
            throw new CodexProtocolException("Model catalog response is not valid JSON.");
        }
    }

    internal static IReadOnlyList<CodexModelCatalogEntry> ParseModelCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array ||
            models.GetArrayLength() > 4096)
            throw new CodexProtocolException("Invalid model catalog envelope.");
        var entries = new List<CodexModelCatalogEntry>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
                throw new CodexProtocolException("Invalid model catalog entry.");
            var slug = CatalogText(model, "slug");
            if (string.IsNullOrWhiteSpace(slug) || slug.Length > 512 || slug.Any(char.IsControl) || !slugs.Add(slug))
                throw new CodexProtocolException("Invalid or duplicate model catalog identity.");

            IReadOnlyList<CodexModelReasoningEffort>? efforts = null;
            if (model.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind != JsonValueKind.Null)
            {
                if (levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() > 128)
                    throw new CodexProtocolException("Invalid model reasoning levels.");
                var list = new List<CodexModelReasoningEffort>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var level in levels.EnumerateArray())
                {
                    if (level.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Invalid model reasoning effort.");
                    var effort = CatalogText(level, "effort");
                    if (string.IsNullOrWhiteSpace(effort) || effort.Length > 128 || effort.Any(char.IsControl) || !names.Add(effort))
                        throw new CodexProtocolException("Invalid or duplicate model reasoning effort.");
                    list.Add(new(effort, CatalogText(level, "description")));
                }
                efforts = list.AsReadOnly();
            }

            IReadOnlyList<string>? modalities = null;
            if (model.TryGetProperty("input_modalities", out var inputs) && inputs.ValueKind != JsonValueKind.Null)
            {
                if (inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() > 128 ||
                    inputs.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new CodexProtocolException("Invalid model input modalities.");
                modalities = Array.AsReadOnly(inputs.EnumerateArray().Select(value => value.GetString()!).ToArray());
            }
            entries.Add(new(slug, CatalogText(model, "display_name"), CatalogText(model, "description"),
                CatalogText(model, "visibility"), CatalogText(model, "default_reasoning_level"), efforts, modalities,
                CatalogBool(model, "supported_in_api"), CatalogLong(model, "context_window"), CatalogLong(model, "max_context_window"),
                CatalogBool(model, "support_verbosity"), CatalogBool(model, "supports_reasoning_summary_parameter"), model.Clone()));
        }
        return entries.AsReadOnly();
    }

    private static string? CatalogText(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool? CatalogBool(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
    private static long? CatalogLong(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number > 0
            ? number : null;
}
