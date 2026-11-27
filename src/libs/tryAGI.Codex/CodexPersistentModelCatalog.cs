using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Verified host account and provider identity for a persistent catalog. The host must advance
/// SelectionEpoch on every login/logout/switch, EntitlementRevision on entitlement changes, and ProviderRevision
/// on effective provider or network-policy changes. Only an opaque digest is stored; no OAuth tokens are accepted here.</summary>
public sealed record CodexModelCatalogCacheScope(
    [property: JsonIgnore] string OwnerScope,
    [property: JsonIgnore] string SelectionEpoch,
    [property: JsonIgnore] string AccountId,
    [property: JsonIgnore] string UserId,
    [property: JsonIgnore] string EntitlementRevision,
    [property: JsonIgnore] string ProviderRevision,
    [property: JsonIgnore] string? Email = null,
    [property: JsonIgnore] string? PlanType = null,
    [property: JsonIgnore] string? RawPlanType = null,
    bool Restricted = false)
{
    /// <inheritdoc />
    public override string ToString() => "CodexModelCatalogCacheScope [redacted]";

    internal string Digest(string clientVersion, Uri backend, string originator)
    {
        foreach (var value in new[] { OwnerScope, SelectionEpoch, AccountId, UserId, EntitlementRevision, ProviderRevision })
            if (!Bounded(value)) throw new CodexProtocolException("Incomplete persistent model catalog identity.");
        foreach (var value in new[] { Email, PlanType, RawPlanType })
            if (value is not null && !Bounded(value)) throw new CodexProtocolException("Invalid persistent model catalog identity.");

        // Canonical, unambiguous encoding. SelectionEpoch is an in-flight authorization fence,
        // not part of the stable cache partition; token rotation never changes the partition.
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("codex-native-model-cache-v1");
            writer.WriteStringValue("openai");
            writer.WriteStringValue("chatgpt");
            writer.WriteStringValue(backend.AbsoluteUri.TrimEnd('/'));
            writer.WriteStringValue(originator);
            writer.WriteStringValue(clientVersion);
            writer.WriteStringValue(OwnerScope);
            writer.WriteStringValue(AccountId);
            writer.WriteStringValue(UserId);
            writer.WriteStringValue(Email);
            writer.WriteStringValue(PlanType);
            writer.WriteStringValue(RawPlanType);
            writer.WriteBooleanValue(Restricted);
            writer.WriteStringValue(EntitlementRevision);
            writer.WriteStringValue(ProviderRevision);
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
    }

    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl);
}

/// <summary>Concrete opt-in file-backed model catalog cache. Use an existing host-owned private directory
/// (owner-only on Unix, appropriately restricted ACL on Windows). Cooperating processes share an exclusive
/// per-identity lock; the host is responsible for securing the directory and its ancestors against external writers.
/// Cache files contain raw provider metadata, not credentials, and are never execution or entitlement authority.</summary>
public sealed class CodexFileModelCatalogCache
{
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode NonOwner = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private readonly string directory;
    private readonly TimeSpan ttl;
    internal TimeProvider Clock { get; }

    /// <summary>Creates a cache using a private host-provisioned directory and a bounded freshness lifetime.</summary>
    public CodexFileModelCatalogCache(string directory, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A private absolute cache directory is required.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
        this.ttl = ttl ?? TimeSpan.FromMinutes(5);
        if (this.ttl < TimeSpan.Zero || this.ttl > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(ttl));
        Clock = timeProvider ?? TimeProvider.System;
        if (!SafeDirectory()) throw new ArgumentException("The model cache directory must already exist and be private.", nameof(directory));
    }

    private bool SafeDirectory()
    {
        var attributes = File.GetAttributes(directory);
        return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0 &&
            (OperatingSystem.IsWindows() || (File.GetUnixFileMode(directory) & NonOwner) == 0);
    }

    internal bool IsFresh(CodexModelCatalogSnapshot value)
    {
        var age = Clock.GetUtcNow() - value.ObservedAt;
        return ttl > TimeSpan.Zero && age >= TimeSpan.Zero && age < ttl;
    }

    internal Lease? TryAcquire(string digest)
    {
        try
        {
            if (!SafeDirectory()) return null;
            var path = Path.Combine(directory, digest + ".json");
            var lockPath = path + ".lock";
            if (!SafeFileOrMissing(lockPath)) return null;
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            var stream = new FileStream(lockPath, options);
            return new(this, path, digest, stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static bool SafeFileOrMissing(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & NonOwner) == 0);
        }
        catch (FileNotFoundException) { return true; }
    }

    internal sealed class Lease(CodexFileModelCatalogCache owner, string path, string digest, FileStream gate) : IDisposable
    {
        internal async Task<CodexModelCatalogSnapshot?> ReadAsync(string clientVersion, int maximumBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!SafeFileOrMissing(path)) return null;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 || stream.Length > maximumBytes) return null;
                var bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !Matches(root, "identity", digest) || !Matches(root, "client_version", clientVersion) ||
                    !Matches(root, "provider_id", "openai") ||
                    !root.TryGetProperty("fetched_at", out var fetched) ||
                    fetched.ValueKind != JsonValueKind.String || !fetched.TryGetDateTimeOffset(out var observed) ||
                    observed == DateTimeOffset.MinValue || observed > owner.Clock.GetUtcNow()) return null;
                var tag = root.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String ? etag.GetString() : null;
                if (tag is not null && (!EntityTagHeaderValue.TryParse(tag, out var parsed) || parsed.Tag == "*")) return null;
                return new("openai", clientVersion, observed, tag, CodexClient.ParseModelCatalog(root));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { return null; }
        }

        // A refresh must invalidate the previous on-disk generation before dispatch. A crash
        // or rejected refresh then leaves a cache miss instead of an apparently fresh catalog.
        internal bool TryInvalidate()
        {
            try
            {
                if (!SafeFileOrMissing(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return false; }
        }

        internal async Task StoreAsync(CodexModelCatalogSnapshot snapshot, int maximumBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var memory = new MemoryStream();
                using (var writer = new Utf8JsonWriter(memory))
                {
                    writer.WriteStartObject();
                    writer.WriteString("identity", digest);
                    writer.WriteString("provider_id", snapshot.ProviderId);
                    writer.WriteString("client_version", snapshot.ClientVersion);
                    writer.WriteString("fetched_at", snapshot.ObservedAt);
                    writer.WriteString("etag", snapshot.ETag);
                    writer.WriteStartArray("models");
                    foreach (var model in snapshot.Models) model.Raw.WriteTo(writer);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                if (memory.Length > maximumBytes || !SafeFileOrMissing(path)) return;
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
                await using (var stream = new FileStream(temp, options))
                {
                    await stream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* Cache storage failures never authorize fallback to another provider. */ }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }

        private static bool Matches(JsonElement root, string name, string value) =>
            root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() == value;

        public void Dispose() => gate.Dispose();
    }
}

public sealed partial class CodexClient
{
    /// <summary>Discovers or revalidates an explicitly scoped persistent ChatGPT model catalog using the
    /// existing OAuth HTTP transport. The host must supply current verified account, entitlement, and provider
    /// metadata on every capture. No stale fallback, implicit login, API-key fallback, or credential persistence.</summary>
    public async Task<CodexModelCatalogSnapshot> GetPersistentModelCatalogAsync(string clientVersion,
        CodexFileModelCatalogCache cache, Func<CancellationToken, Task<CodexModelCatalogCacheScope>> readScope,
        bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(readScope);
        var backend = ValidateModelCatalogEndpoint(clientVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        var ct = deadline.Token;
        var scope = await readScope(ct).ConfigureAwait(false);
        var key = scope.Digest(clientVersion, backend, options.Originator);
        var auth = await VerifyPersistentCatalogScopeAsync(scope, readScope, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        using var lease = cache.TryAcquire(key);
        // When a force-refresh cannot invalidate a shared cached generation, reject before dispatch
        // rather than leaving an old entry apparently valid after a failed revalidation.
        if (forceRefresh && lease is null) throw new CodexProtocolException("Persistent catalog cache is busy or unavailable.");
        var maximumBytes = options.MaximumMessageBytes + 16_384;
        var cached = lease is null ? null : await lease.ReadAsync(clientVersion, maximumBytes, ct).ConfigureAwait(false);
        if (!forceRefresh && cached is not null && cache.IsFresh(cached))
        {
            await VerifyPersistentCatalogScopeAsync(scope, readScope, ct).ConfigureAwait(false);
            return cached;
        }

        var writable = lease?.TryInvalidate() == true;
        if (forceRefresh && !writable) throw new CodexProtocolException("Persistent catalog could not be invalidated.");
        auth = await VerifyPersistentCatalogScopeAsync(scope, readScope, ct).ConfigureAwait(false);
        var result = await FetchModelCatalogAsync(backend, auth, clientVersion, cached, cache.Clock, ct).ConfigureAwait(false);
        await VerifyPersistentCatalogScopeAsync(scope, readScope, ct).ConfigureAwait(false);
        if (writable) await lease!.StoreAsync(result, maximumBytes, ct).ConfigureAwait(false);
        await VerifyPersistentCatalogScopeAsync(scope, readScope, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Creates an explicitly registered native model/list source backed by the persistent ChatGPT cache.</summary>
    public CodexModelCatalogSource CreatePersistentModelCatalogSource(string clientVersion, CodexFileModelCatalogCache cache,
        Func<CancellationToken, Task<CodexModelCatalogCacheScope>> readScope)
        => new("openai", CodexProviderAuthMode.ChatGptSubscription,
            ct => GetPersistentModelCatalogAsync(clientVersion, cache, readScope, cancellationToken: ct));

    private async Task<CodexCredentials> VerifyPersistentCatalogScopeAsync(CodexModelCatalogCacheScope captured,
        Func<CancellationToken, Task<CodexModelCatalogCacheScope>> readScope, CancellationToken ct)
    {
        var auth = await credentials.GetCredentialsAsync(ct).ConfigureAwait(false);
        var current = await readScope(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (captured != current || captured.AccountId != auth.AccountId ||
            captured.UserId != auth.ChatGptUserId || captured.Restricted != auth.IsFedRampAccount)
            throw new CodexProtocolException("Authorization changed during persistent model discovery.");
        return auth;
    }
}
