using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>An encrypted, revision-bound record for a host-owned durable vault.</summary>
public sealed record CodexOAuthVaultRecord(string Version, [property: JsonIgnore] ReadOnlyMemory<byte> Ciphertext)
{
    /// <inheritdoc />
    public override string ToString() => "CodexOAuthVaultRecord [redacted]";
}

/// <summary>Host-provided cross-process vault. The host must durably serialize all access to a session ID.</summary>
public interface ICodexOAuthVault
{
    /// <summary>Acquires the same exclusive lease for login, logout, reads and refresh, across cooperating processes.</summary>
    ValueTask<ICodexOAuthVaultLease> AcquireAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Atomic durable encrypted record storage. The vault never receives plaintext credentials or the key.</summary>
public interface ICodexOAuthVaultLease : IAsyncDisposable
{
    /// <summary>Reads the current encrypted record; null means absent.</summary>
    ValueTask<CodexOAuthVaultRecord?> ReadAsync(CancellationToken cancellationToken);
    /// <summary>Durably compares and exchanges the exact record version; null replacement deletes it.</summary>
    ValueTask<bool> CompareExchangeAsync(string? expectedVersion, CodexOAuthVaultRecord? replacement, CancellationToken cancellationToken);
}

/// <summary>Redacted session status for host account selection.</summary>
public sealed record CodexOAuthSessionInfo(string SessionId, string Revision, DateTimeOffset ExpiresAt, bool RefreshPending);

/// <summary>Authenticated encryption over an atomic host vault, sharing a lease with the existing refresh coordinator.
/// The host supplies a separately protected 256-bit key and provides durable CAS and cross-process serialization.
/// No file, operating-system keyring, login, provider request or fallback is implicit.</summary>
public sealed class CodexEncryptedOAuthSessionStore : ICodexOAuthSessionStore, IDisposable
{
    private const int MaxPayload = 262_144;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly ICodexOAuthVault vault;
    private readonly byte[] key;
    private readonly object keyGate = new();
    private bool disposed;

    /// <summary>Creates an encrypted session adapter. The input key is copied, never persisted by this adapter.</summary>
    public CodexEncryptedOAuthSessionStore(ICodexOAuthVault vault, ReadOnlySpan<byte> encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (encryptionKey.Length != 32) throw new ArgumentException("A 256-bit vault key is required.", nameof(encryptionKey));
        this.vault = vault;
        key = encryptionKey.ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<ICodexOAuthSessionLease> AcquireAsync(string sessionId, CancellationToken cancellationToken)
        => await AcquireCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<Lease> AcquireCoreAsync(string sessionId, CancellationToken ct)
    {
        ValidateSessionId(sessionId);
        lock (keyGate) ObjectDisposedException.ThrowIf(disposed, this);
        return new Lease(this, sessionId, await vault.AcquireAsync(sessionId, ct).ConfigureAwait(false));
    }

    /// <summary>Returns non-secret metadata, including a pending attempt rather than assuming the session is usable.</summary>
    public async ValueTask<CodexOAuthSessionInfo?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var state = await lease.ReadAsync(cancellationToken).ConfigureAwait(false);
        return state is null ? null : new(sessionId, state.Revision, state.Tokens.ExpiresAt, state.RefreshAttemptId is not null);
    }

    /// <summary>Installs explicitly acquired credentials, only if the expected generation is still current.
    /// A null expected revision allows initial sign-in only. Pending rotation may be replaced only by explicit reauthorization.</summary>
    public async ValueTask<bool> TrySignInAsync(string sessionId, string? expectedRevision, CodexOAuthTokens tokens,
        CancellationToken cancellationToken = default)
    {
        ValidateTokens(tokens);
        await using var lease = await AcquireCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var current = await lease.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current.State?.Revision != expectedRevision) return false;
        // A stable session identity is never silently repointed to a different account.
        if (current.State is { } state && !SameIdentity(state.Tokens, tokens))
            throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);
        return await lease.WriteAsync(current.Record, new(NewRevision(), tokens), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs out the exact captured generation, including a pending rotation. An older request cannot delete a newer login.</summary>
    public async ValueTask<bool> TrySignOutAsync(string sessionId, string expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await AcquireCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var current = await lease.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        return current.State?.Revision == expectedRevision &&
            await lease.WriteAsync(current.Record, null, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Lease(CodexEncryptedOAuthSessionStore owner, string id, ICodexOAuthVaultLease inner) : ICodexOAuthSessionLease
    {
        public async ValueTask<CodexOAuthSessionState?> ReadAsync(CancellationToken cancellationToken)
            => (await ReadCurrentAsync(cancellationToken).ConfigureAwait(false)).State;

        internal async ValueTask<(CodexOAuthVaultRecord? Record, CodexOAuthSessionState? State)> ReadCurrentAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var record = await inner.ReadAsync(ct).ConfigureAwait(false);
            return (record, record is null ? null : owner.Decrypt(id, record));
        }

        internal ValueTask<bool> WriteAsync(CodexOAuthVaultRecord? previous, CodexOAuthSessionState? next, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return inner.CompareExchangeAsync(previous?.Version, next is null ? null : owner.Encrypt(id, next), ct);
        }

        public async ValueTask<bool> TryBeginRefreshAsync(string expectedRevision, Guid attemptId, CancellationToken cancellationToken)
        {
            if (attemptId == Guid.Empty) throw new ArgumentException("Refresh attempt is required.", nameof(attemptId));
            var current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current.State is not { RefreshAttemptId: null } state || state.Revision != expectedRevision) return false;
            return await WriteAsync(current.Record, new(state.Revision, state.Tokens, attemptId), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<bool> TryCompleteRefreshAsync(string expectedRevision, Guid attemptId, CodexOAuthTokens tokens, CancellationToken cancellationToken)
        {
            ValidateTokens(tokens);
            if (attemptId == Guid.Empty) return false;
            var current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current.State is not { } state || state.Revision != expectedRevision ||
                state.RefreshAttemptId != attemptId || !SameIdentity(state.Tokens, tokens)) return false;
            return await WriteAsync(current.Record, new(NewRevision(), tokens), cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private CodexOAuthVaultRecord Encrypt(string id, CodexOAuthSessionState state)
    {
        var version = NewRevision();
        using var memory = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(memory))
            {
                writer.WriteStartObject();
                writer.WriteString("revision", state.Revision);
                if (state.RefreshAttemptId is { } attempt) writer.WriteString("pending", attempt);
                writer.WriteString("access", state.Tokens.AccessToken);
                writer.WriteString("refresh", state.Tokens.RefreshToken);
                writer.WriteString("account", state.Tokens.AccountId);
                writer.WriteString("user", state.Tokens.ChatGptUserId);
                writer.WriteBoolean("restricted", state.Tokens.IsFedRampAccount);
                writer.WriteString("expires", state.Tokens.ExpiresAt);
                writer.WriteEndObject();
            }
            if (memory.Length > MaxPayload) throw InvalidRecord();
            var output = new byte[1 + NonceSize + TagSize + (int)memory.Length];
            output[0] = 1;
            var nonce = output.AsSpan(1, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = CreateCipher();
            aes.Encrypt(nonce, memory.GetBuffer().AsSpan(0, (int)memory.Length),
                output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), AssociatedData(id, version));
            return new(version, output);
        }
        finally { CryptographicOperations.ZeroMemory(memory.GetBuffer()); }
    }

    private CodexOAuthSessionState Decrypt(string id, CodexOAuthVaultRecord record)
    {
        var data = record.Ciphertext.Span;
        if (!Guid.TryParseExact(record.Version, "N", out _) || data.Length <= 1 + NonceSize + TagSize ||
            data.Length > 1 + NonceSize + TagSize + MaxPayload || data[0] != 1) throw InvalidRecord();
        var plain = new byte[data.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = CreateCipher();
            aes.Decrypt(data.Slice(1, NonceSize), data[(1 + NonceSize + TagSize)..],
                data.Slice(1 + NonceSize, TagSize), plain, AssociatedData(id, record.Version));
            using var json = JsonDocument.Parse(plain);
            var value = json.RootElement;
            var revision = value.GetProperty("revision").GetString();
            if (!Guid.TryParseExact(revision, "N", out _)) throw InvalidRecord();
            Guid? pending = value.TryGetProperty("pending", out var attempt) ? attempt.GetGuid() : null;
            if (pending == Guid.Empty) throw InvalidRecord();
            var tokens = new CodexOAuthTokens(value.GetProperty("access").GetString()!,
                value.GetProperty("refresh").GetString()!, value.GetProperty("account").GetString()!,
                value.GetProperty("expires").GetDateTimeOffset(), value.GetProperty("user").GetString(),
                value.GetProperty("restricted").GetBoolean());
            ValidateTokens(tokens);
            return new(revision!, tokens, pending);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw InvalidRecord(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private AesGcm CreateCipher()
    {
        lock (keyGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new AesGcm(key, TagSize);
        }
    }

    private static byte[] AssociatedData(string id, string version) => Encoding.UTF8.GetBytes("codex-oauth-v1\n" + id + "\n" + version);
    private static string NewRevision() => Guid.NewGuid().ToString("N");
    private static bool SameIdentity(CodexOAuthTokens a, CodexOAuthTokens b) =>
        a.AccountId == b.AccountId && a.IsFedRampAccount == b.IsFedRampAccount &&
        (a.ChatGptUserId is null || a.ChatGptUserId == b.ChatGptUserId);
    private static void ValidateSessionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            throw new ArgumentException("Invalid session ID.", nameof(id));
    }
    private static void ValidateTokens(CodexOAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!Valid(tokens.AccessToken, 65_536) || !Valid(tokens.RefreshToken, 65_536) ||
            !Valid(tokens.AccountId, 512) || tokens.ChatGptUserId is { } user && !Valid(user, 512))
            throw InvalidRecord();
    }
    private static bool Valid(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static CodexOAuthRefreshException InvalidRecord() => new(CodexOAuthRefreshFailure.InvalidCredentials);

    /// <summary>Clears the adapter's copy of the vault key. The host is responsible for the original key.</summary>
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
