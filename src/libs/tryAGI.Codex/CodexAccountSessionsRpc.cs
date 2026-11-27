using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>One provider-verified workspace within a saved login. A directory entry is not proof of usable credentials.</summary>
public sealed record CodexAccountSessionWorkspace(string AccountId, string? Name = null, string? ImageUrl = null, string? Kind = null);

/// <summary>Persisted account-session metadata. OAuth tokens are kept in a separate secure vault.</summary>
public sealed record CodexAccountSessionEntry(string SessionId, string? UserId, string? Email, string? DisplayName,
    string? ImageUrl, long LastUsedAt, string? SelectedWorkspaceAccountId,
    IReadOnlyList<CodexAccountSessionWorkspace> Workspaces)
{
    /// <inheritdoc />
    public override string ToString() => "CodexAccountSessionEntry [redacted]";
}

/// <summary>Host-owned durable directory of saved logins for one authorized connection owner.
/// Revision changes on any directory write; SelectionEpoch changes on login, logout or selection changes.</summary>
public sealed record CodexAccountSessionDirectorySnapshot(string Revision, string SelectionEpoch, string? ActiveSessionId,
    IReadOnlyList<CodexAccountSessionEntry> Sessions)
{
    /// <inheritdoc />
    public override string ToString() => "CodexAccountSessionDirectorySnapshot [redacted]";
}

/// <summary>A secure vault observation, not a credential. Its opaque version must change on refresh, pending rotation or removal.</summary>
public sealed record CodexAccountSessionCredentialFence(
    [property: JsonIgnore] string VaultVersion,
    [property: JsonIgnore] string SessionId,
    [property: JsonIgnore] string AccountId,
    [property: JsonIgnore] string UserId,
    DateTimeOffset ExpiresAt, bool RefreshPending, bool Restricted)
{
    /// <inheritdoc />
    public override string ToString() => "CodexAccountSessionCredentialFence [redacted]";
}

/// <summary>Authenticated, owner-scoped durable session directory.
/// The host coordinates directory and secure vault mutations with login/logout/refresh across processes.
/// This interface must not expose arbitrary users' directories or start login as a side effect of a read.</summary>
public interface ICodexAccountSessionDirectoryStorage
{
    /// <summary>Reads a consistent snapshot. Explicit metadata refresh may update workspace membership but must not select another account.</summary>
    Task<CodexAccountSessionDirectorySnapshot> ReadAsync(bool refreshWorkspaceMetadata, CancellationToken cancellationToken);

    /// <summary>Returns a verified current vault fence for exactly the requested saved login and workspace, or null if unavailable.
    /// No token exchange, implicit SSO, or paid provider fallback is authorized by this operation.</summary>
    Task<CodexAccountSessionCredentialFence?> ReadCredentialFenceAsync(string sessionId, string accountId, CancellationToken cancellationToken);

    /// <summary>Atomically commits the directory replacement only if both directory revision and credential fence still match.
    /// The host must also recheck account/workspace policy, invalidate previous selection authority and coordinate vault/selection locks.
    /// A thrown write may have committed; callers must not blindly retry it.</summary>
    Task<bool> TryCommitSelectionAsync(string expectedRevision, CodexAccountSessionCredentialFence credential,
        CodexAccountSessionDirectorySnapshot replacement, CancellationToken cancellationToken);
}

/// <summary>Native accountSession/list and accountSession/switch. Only previously authorized, usable ChatGPT credentials
/// may be selected. The host provides durable CAS storage; the facade implements the selection state transition.</summary>
public sealed class CodexAccountSessionsRpc
{
    private readonly ICodexAccountSessionDirectoryStorage storage;
    private readonly Func<string, string, CancellationToken, Task> authorizeSwitch;
    private readonly TimeProvider time;

    /// <summary>Creates an owner-scoped session facade. The switch authorization is required even for a previously saved account.</summary>
    public CodexAccountSessionsRpc(ICodexAccountSessionDirectoryStorage storage,
        Func<string, string, CancellationToken, Task> authorizeSwitch, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorizeSwitch);
        this.storage = storage;
        this.authorizeSwitch = authorizeSwitch;
        time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether the facade implements this native method.</summary>
    public static bool Handles(string method) => method is "accountSession/list" or "accountSession/switch";

    /// <summary>Reads or selects an authorized saved session. Browser/SSO handoff must be explicitly integrated by the host.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported account session method.");
        var p = parameters is { ValueKind: not JsonValueKind.Null } supplied ? supplied : Element(new JsonObject());
        Fields(p, method == "accountSession/list" ? ["refreshWorkspaceMetadata"] : ["sessionId", "accountId", "allowBrowserLogin"]);
        if (method == "accountSession/list")
        {
            var refresh = Flag(p, "refreshWorkspaceMetadata");
            var snapshot = await storage.ReadAsync(refresh, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Validate(snapshot);
            return Element(Project(snapshot));
        }

        var sessionId = Required(p, "sessionId");
        var accountId = Required(p, "accountId");
        var allowBrowserLogin = Flag(p, "allowBrowserLogin");
        // Admission comes before inspecting credentials or revealing directory membership.
        await authorizeSwitch(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var before = await storage.ReadAsync(false, cancellationToken).ConfigureAwait(false);
        Validate(before);
        var session = before.Sessions.SingleOrDefault(candidate => candidate.SessionId == sessionId);
        if (session is null || session.UserId is null || !session.Workspaces.Any(workspace => workspace.AccountId == accountId))
            throw Invalid("Unknown or unverified account session workspace.");
        var credential = await storage.ReadCredentialFenceAsync(sessionId, accountId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is null || credential.RefreshPending || credential.ExpiresAt <= time.GetUtcNow() ||
            credential.Restricted || !Bounded(credential.VaultVersion) ||
            credential.SessionId != sessionId || credential.AccountId != accountId || credential.UserId != session.UserId)
            throw new CodexThreadRpcException(-32000, allowBrowserLogin
                ? "Workspace credentials are unavailable; browser login is not registered."
                : "Workspace credentials are unavailable; explicit authorization is required.");

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        // Never reuse an old epoch, even when switching back to a previously selected workspace.
        // An idempotent selection retains its epoch, but still goes through the storage credential fence.
        var changed = before.ActiveSessionId != sessionId || session.SelectedWorkspaceAccountId != accountId;
        var replacement = new CodexAccountSessionDirectorySnapshot(NewRevision(),
            changed ? NewRevision() : before.SelectionEpoch, sessionId,
            before.Sessions.Select(candidate => candidate.SessionId == sessionId
                ? candidate with { SelectedWorkspaceAccountId = accountId, LastUsedAt = Math.Max(candidate.LastUsedAt, now) }
                : candidate).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        bool committed;
        // Do not cancel an admitted durable write just because the requesting client disconnects.
        // A deadline or exception is an unknown outcome, never a reason to retry or report success.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
        try
        {
            committed = await storage.TryCommitSelectionAsync(before.Revision, credential, replacement, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new CodexThreadRpcException(-32000, "Account selection commit outcome is unconfirmed.");
        }
        if (!committed) throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);
        cancellationToken.ThrowIfCancellationRequested();
        return Element(Project(replacement));
    }

    internal static void Validate(CodexAccountSessionDirectorySnapshot snapshot)
    {
        if (!Bounded(snapshot.Revision) || !Bounded(snapshot.SelectionEpoch) || snapshot.Sessions.Count > 256 ||
            snapshot.ActiveSessionId is { } active && !Bounded(active))
            throw Invalid("Invalid account session directory.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in snapshot.Sessions)
        {
            if (!Bounded(session.SessionId) || !ids.Add(session.SessionId) || session.UserId is { } user && !Bounded(user) ||
                session.LastUsedAt < 0 || session.Workspaces.Count > 256 ||
                !ValidOptional(session.Email) || !ValidOptional(session.DisplayName) || !ValidImage(session.ImageUrl))
                throw Invalid("Invalid account session entry.");
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var workspace in session.Workspaces)
            {
                if (!Bounded(workspace.AccountId) || !accounts.Add(workspace.AccountId) ||
                    !ValidOptional(workspace.Name) || !ValidImage(workspace.ImageUrl) ||
                    workspace.Kind is not (null or "personal" or "workspace"))
                    throw Invalid("Invalid account session workspace.");
            }
            if (session.SelectedWorkspaceAccountId is { } selected && !accounts.Contains(selected))
                throw Invalid("Selected workspace is not part of the saved session.");
        }
        if (snapshot.ActiveSessionId is { } id &&
            (!ids.Contains(id) || snapshot.Sessions.Single(session => session.SessionId == id).SelectedWorkspaceAccountId is null))
            throw Invalid("Invalid active account session.");
    }

    private static JsonObject Project(CodexAccountSessionDirectorySnapshot snapshot)
    {
        var sessions = new JsonArray();
        foreach (var session in snapshot.Sessions)
        {
            var workspaces = new JsonArray();
            foreach (var workspace in session.Workspaces)
                workspaces.Add((JsonNode)new JsonObject
                {
                    ["accountId"] = workspace.AccountId, ["name"] = workspace.Name,
                    ["imageUrl"] = workspace.ImageUrl, ["kind"] = workspace.Kind
                });
            sessions.Add((JsonNode)new JsonObject
            {
                ["sessionId"] = session.SessionId, ["email"] = session.Email, ["userId"] = session.UserId,
                ["displayName"] = session.DisplayName, ["imageUrl"] = session.ImageUrl, ["lastUsedAt"] = session.LastUsedAt,
                ["isActive"] = session.SessionId == snapshot.ActiveSessionId,
                ["selectedWorkspaceAccountId"] = session.SelectedWorkspaceAccountId, ["workspaces"] = workspaces
            });
        }
        return new JsonObject { ["activeSessionId"] = snapshot.ActiveSessionId, ["sessions"] = sessions };
    }

    private static string NewRevision() => Guid.NewGuid().ToString("N");
    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    private static bool ValidOptional(string? value) => value is null || Bounded(value);
    private static bool ValidImage(string? value) => value is null || value.Length <= 4096 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static string Required(JsonElement p, string key) =>
        p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && Bounded(value.GetString())
            ? value.GetString()! : throw Invalid("Invalid account session identity.");
    private static bool Flag(JsonElement p, string key)
    {
        if (!p.TryGetProperty(key, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Invalid account session flag.");
        return value.GetBoolean();
    }
    private static void Fields(JsonElement p, string[] allowed)
    {
        if (p.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(p.GetRawText()) > 8192)
            throw Invalid("Invalid account session parameters.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in p.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal)) throw Invalid("Unknown or duplicate account session parameter.");
    }
    private static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}
