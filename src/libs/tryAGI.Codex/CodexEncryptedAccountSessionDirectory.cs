using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Concrete encrypted account directory over the same durable vault protocol as OAuth credentials.
/// The host provisions the owner scope, private key, credential ID mapping, and a policy lease that remains
/// authoritative until disposed. Consumers must fence credential admission by the persisted SelectionEpoch.
/// The local durable lifecycle uses an encrypted cleanup journal; all cooperating readers and writers
/// must use its journal-aware directory and credential admission. Remote OAuth revocation remains a host concern.</summary>
public sealed partial class CodexEncryptedAccountSessionDirectory : ICodexAccountSessionDirectoryStorage, IDisposable
{
    private const int MaximumPayload = 262_144;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string EmptyRevision = "empty-v1";
    private readonly ICodexOAuthVault directoryVault;
    private readonly ICodexOAuthSessionStore credentials;
    private readonly Func<string, string, string> credentialId;
    private readonly Func<CodexAccountSessionCredentialFence, CancellationToken, ValueTask<IAsyncDisposable?>> acquirePolicy;
    private readonly string directoryId;
    private readonly TimeProvider clock;
    private readonly byte[] key;
    private readonly object keyGate = new();
    private bool disposed;

    /// <summary>Creates an owner-scoped directory. The credential mapping is host-owned and immutable.
    /// Policy acquisition must not acquire directory or credential vault locks: lock order is directory,
    /// credential, then policy. Policy updates must honor that lease. Null means denied.</summary>
    public CodexEncryptedAccountSessionDirectory(ICodexOAuthVault directoryVault, ICodexOAuthSessionStore credentials,
        string ownerScope, ReadOnlySpan<byte> encryptionKey, Func<string, string, string> credentialId,
        Func<CodexAccountSessionCredentialFence, CancellationToken, ValueTask<IAsyncDisposable?>> acquirePolicy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(directoryVault);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(acquirePolicy);
        if (string.IsNullOrWhiteSpace(ownerScope) || ownerScope.Length > 512 || ownerScope.Any(char.IsControl))
            throw new ArgumentException("Invalid account directory owner scope.", nameof(ownerScope));
        if (encryptionKey.Length != 32) throw new ArgumentException("A 256-bit directory key is required.", nameof(encryptionKey));
        this.directoryVault = directoryVault;
        this.credentials = credentials;
        this.credentialId = credentialId;
        this.acquirePolicy = acquirePolicy;
        directoryId = "account-directory-v1-" + Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(ownerScope)));
        key = encryptionKey.ToArray();
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<CodexAccountSessionDirectorySnapshot> ReadAsync(bool refreshWorkspaceMetadata, CancellationToken cancellationToken)
    {
        if (refreshWorkspaceMetadata)
            throw new CodexThreadRpcException(-32000, "Workspace metadata refresh is not registered.");
        await using var lease = await AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDirectoryAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers an already authorized saved login without switching the active session.
    /// Existing entries are never overwritten; a stale caller must re-read rather than replay an uncertain write.
    /// Workspace membership is verified by the host. The selected workspace must have a usable matching credential.</summary>
    public async Task<bool> TryRegisterSessionAsync(string expectedRevision, CodexAccountSessionEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry with { Workspaces = Array.AsReadOnly(entry.Workspaces.ToArray()) };
        await using var directory = await AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision || current.Sessions.Any(session => session.SessionId == entry.SessionId))
            return false;
        var next = new CodexAccountSessionDirectorySnapshot(NewRevision(), NewRevision(), current.ActiveSessionId,
            [.. current.Sessions, entry]);
        CodexAccountSessionsRpc.Validate(next);
        if (entry.UserId is null || entry.SelectedWorkspaceAccountId is not { } account) return false;
        await using var credential = await AcquireCredentialAsync(entry.SessionId, account, cancellationToken).ConfigureAwait(false);
        var fence = await ObserveAsync(credential, entry, account, cancellationToken).ConfigureAwait(false);
        if (!Usable(fence)) return false;
        await using var policy = await acquirePolicy(fence!, cancellationToken).ConfigureAwait(false);
        if (policy is null || !Usable(fence)) return false;
        return await PersistAsync(directory, current, next, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CodexAccountSessionCredentialFence?> ReadCredentialFenceAsync(string sessionId, string accountId,
        CancellationToken cancellationToken)
    {
        await using var directory = await AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        var session = Find(current, sessionId, accountId);
        if (session is null) return null;
        await using var credential = await AcquireCredentialAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        return await ObserveAsync(credential, session, accountId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryCommitSelectionAsync(string expectedRevision, CodexAccountSessionCredentialFence credential,
        CodexAccountSessionDirectorySnapshot replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(replacement);
        // Snapshot the caller-owned list before the first await.
        replacement = replacement with { Sessions = Array.AsReadOnly(replacement.Sessions.Select(entry =>
            entry with { Workspaces = Array.AsReadOnly(entry.Workspaces.ToArray()) }).ToArray()) };
        CodexAccountSessionsRpc.Validate(replacement);
        await using var directory = await AcquireDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision) return false;
        var session = Find(current, credential.SessionId, credential.AccountId);
        if (session is null || !ValidTransition(current, replacement, credential)) return false;
        await using var sessionLease = await AcquireCredentialAsync(credential.SessionId, credential.AccountId, cancellationToken).ConfigureAwait(false);
        var observed = await ObserveAsync(sessionLease, session, credential.AccountId, cancellationToken).ConfigureAwait(false);
        if (observed != credential || !Usable(observed)) return false;
        // Hold the credential's cross-process lease until the directory's durable CAS settles.
        // A pending/rotated/revoked generation cannot race a successful selection commit.
        await using var policy = await acquirePolicy(credential, cancellationToken).ConfigureAwait(false);
        if (policy is null || !Usable(observed)) return false;
        return await PersistAsync(directory, current, replacement, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ICodexOAuthVaultLease> AcquireDirectoryAsync(CancellationToken ct)
    {
        lock (keyGate) ObjectDisposedException.ThrowIf(disposed, this);
        return directoryVault.AcquireAsync(directoryId, ct);
    }

    private ValueTask<ICodexOAuthSessionLease> AcquireCredentialAsync(string sessionId, string accountId, CancellationToken ct)
    {
        var id = credentialId(sessionId, accountId);
        if (string.IsNullOrWhiteSpace(id) || id == directoryId || id.Length > 256 || id.Any(char.IsControl))
            throw new CodexProtocolException("Invalid account credential mapping.");
        return credentials.AcquireAsync(id, ct);
    }

    private static CodexAccountSessionEntry? Find(CodexAccountSessionDirectorySnapshot snapshot, string sessionId, string accountId) =>
        snapshot.Sessions.SingleOrDefault(session => session.SessionId == sessionId && session.UserId is not null &&
            session.Workspaces.Any(workspace => workspace.AccountId == accountId));

    private static async Task<CodexAccountSessionCredentialFence?> ObserveAsync(ICodexOAuthSessionLease lease,
        CodexAccountSessionEntry session, string accountId, CancellationToken ct)
    {
        var state = await lease.ReadAsync(ct).ConfigureAwait(false);
        if (state is null || session.UserId is null || state.Tokens.AccountId != accountId ||
            state.Tokens.ChatGptUserId != session.UserId || !Guid.TryParseExact(state.Revision, "N", out _) ||
            string.IsNullOrWhiteSpace(state.Tokens.AccessToken) || string.IsNullOrWhiteSpace(state.Tokens.RefreshToken)) return null;
        // The credential revision changes on replacement; the pending attempt distinguishes
        // a rotation's in-flight state from the same previously usable token generation.
        var version = state.Revision + ":" + (state.RefreshAttemptId?.ToString("N") ?? "ready");
        return new(version, session.SessionId, accountId, session.UserId,
            state.Tokens.ExpiresAt, state.RefreshAttemptId is not null, state.Tokens.IsFedRampAccount);
    }

    private bool Usable(CodexAccountSessionCredentialFence? fence) => fence is { RefreshPending: false, Restricted: false } &&
        fence.ExpiresAt > clock.GetUtcNow();

    private static bool ValidTransition(CodexAccountSessionDirectorySnapshot current,
        CodexAccountSessionDirectorySnapshot replacement, CodexAccountSessionCredentialFence credential)
    {
        if (replacement.ActiveSessionId != credential.SessionId ||
            replacement.Revision == current.Revision || !Guid.TryParseExact(replacement.Revision, "N", out _) ||
            replacement.Sessions.Count != current.Sessions.Count) return false;
        var previous = Find(current, credential.SessionId, credential.AccountId);
        if (previous is null || previous.UserId != credential.UserId) return false;
        var changed = current.ActiveSessionId != credential.SessionId || previous.SelectedWorkspaceAccountId != credential.AccountId;
        if (changed ? replacement.SelectionEpoch == current.SelectionEpoch || !Guid.TryParseExact(replacement.SelectionEpoch, "N", out _)
            : replacement.SelectionEpoch != current.SelectionEpoch) return false;

        for (var i = 0; i < current.Sessions.Count; i++)
        {
            var before = current.Sessions[i];
            var after = replacement.Sessions[i];
            if (!before.Workspaces.SequenceEqual(after.Workspaces)) return false;
            after = after with { Workspaces = before.Workspaces };
            if (before.SessionId == credential.SessionId)
            {
                if (after.SelectedWorkspaceAccountId != credential.AccountId || after.LastUsedAt < before.LastUsedAt) return false;
                before = before with { SelectedWorkspaceAccountId = credential.AccountId, LastUsedAt = after.LastUsedAt };
            }
            if (before != after) return false;
        }
        return true;
    }

    private async Task<CodexAccountSessionDirectorySnapshot> ReadDirectoryAsync(ICodexOAuthVaultLease lease, CancellationToken ct)
    {
        var record = await lease.ReadAsync(ct).ConfigureAwait(false);
        if (record is null) return new(EmptyRevision, EmptyRevision, null, Array.Empty<CodexAccountSessionEntry>());
        var state = Decrypt(record);
        var snapshot = state.Snapshot;
        if (state.Cleanup is { Count: > 0 })
            return await RecoverCleanupAsync(lease, snapshot, state.Cleanup, ct).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<bool> PersistAsync(ICodexOAuthVaultLease lease, CodexAccountSessionDirectorySnapshot before,
        CodexAccountSessionDirectorySnapshot next, CancellationToken ct, IReadOnlyList<CodexAccountDirectoryCleanup>? cleanup = null)
    {
        ct.ThrowIfCancellationRequested();
        var replacement = Encrypt(next, cleanup);
        return await lease.CompareExchangeAsync(before.Revision == EmptyRevision ? null : before.Revision, replacement, ct).ConfigureAwait(false);
    }

    private CodexOAuthVaultRecord Encrypt(CodexAccountSessionDirectorySnapshot snapshot, IReadOnlyList<CodexAccountDirectoryCleanup>? cleanup)
    {
        var state = new CodexAccountDirectoryState(snapshot.Revision, snapshot.SelectionEpoch, snapshot.ActiveSessionId, snapshot.Sessions, cleanup);
        var plain = JsonSerializer.SerializeToUtf8Bytes(state, CodexAccountDirectoryJson.Default.CodexAccountDirectoryState);
        try
        {
            if (plain.Length > MaximumPayload || !Guid.TryParseExact(snapshot.Revision, "N", out _)) throw Invalid();
            var cipher = new byte[1 + NonceSize + TagSize + plain.Length];
            // Older readers must fail closed during an incomplete lifecycle transaction.
            cipher[0] = cleanup is { Count: > 0 } ? (byte)2 : (byte)1;
            RandomNumberGenerator.Fill(cipher.AsSpan(1, NonceSize));
            using var aes = CreateCipher();
            aes.Encrypt(cipher.AsSpan(1, NonceSize), plain, cipher.AsSpan(1 + NonceSize + TagSize),
                cipher.AsSpan(1 + NonceSize, TagSize), AssociatedData(snapshot.Revision, cipher[0]));
            return new(snapshot.Revision, cipher);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private CodexAccountDirectoryState Decrypt(CodexOAuthVaultRecord record)
    {
        var cipher = record.Ciphertext.Span;
        if (!Guid.TryParseExact(record.Version, "N", out _) || cipher.Length <= 1 + NonceSize + TagSize ||
            cipher.Length > 1 + NonceSize + TagSize + MaximumPayload || cipher[0] is not (1 or 2)) throw Invalid();
        var plain = new byte[cipher.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = CreateCipher();
            aes.Decrypt(cipher.Slice(1, NonceSize), cipher[(1 + NonceSize + TagSize)..],
                cipher.Slice(1 + NonceSize, TagSize), plain, AssociatedData(record.Version, cipher[0]));
            var state = JsonSerializer.Deserialize(plain, CodexAccountDirectoryJson.Default.CodexAccountDirectoryState);
            if (state is null || state.Revision != record.Version || state.Cleanup is { Count: > 256 } ||
                (cipher[0] == 2) != (state.Cleanup is { Count: > 0 })) throw Invalid();
            var snapshot = state.Snapshot;
            CodexAccountSessionsRpc.Validate(snapshot);
            return state with { Sessions = Array.AsReadOnly(snapshot.Sessions.Select(entry =>
                entry with { Workspaces = Array.AsReadOnly(entry.Workspaces.ToArray()) }).ToArray()) };
        }
        catch (Exception error) when (error is CryptographicException or JsonException or CodexThreadRpcException or
            ArgumentException or NullReferenceException)
        { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // Bind the journal format as well as its owner and revision to the authentication tag.
    // Preserve the original v1 domain for settled records and legacy directories.
    private byte[] AssociatedData(string version, byte format) => Encoding.UTF8.GetBytes(
        (format == 2 ? "codex-account-directory-v2\n" : "codex-account-directory-v1\n") + directoryId + "\n" + version);
    private static string NewRevision() => Guid.NewGuid().ToString("N");
    private static CodexProtocolException Invalid() => new("Invalid encrypted account session directory.");

    private AesGcm CreateCipher()
    {
        lock (keyGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new AesGcm(key, TagSize);
        }
    }

    /// <summary>Clears this adapter's copy of the encryption key.</summary>
    public void Dispose()
    {
        lock (keyGate)
        {
            if (disposed) return;
            CryptographicOperations.ZeroMemory(key);
            disposed = true;
        }
    }
}

// The first four members preserve the original on-disk directory schema.
internal sealed record CodexAccountDirectoryState(string Revision, string SelectionEpoch, string? ActiveSessionId,
    IReadOnlyList<CodexAccountSessionEntry> Sessions, IReadOnlyList<CodexAccountDirectoryCleanup>? Cleanup = null)
{
    [JsonIgnore] internal CodexAccountSessionDirectorySnapshot Snapshot => new(Revision, SelectionEpoch, ActiveSessionId, Sessions);
}

internal sealed record CodexAccountDirectoryCleanup(string SessionId, string AccountId, string UserId, string CredentialId, string Revision);

[JsonSerializable(typeof(CodexAccountDirectoryState))]
internal sealed partial class CodexAccountDirectoryJson : JsonSerializerContext;
