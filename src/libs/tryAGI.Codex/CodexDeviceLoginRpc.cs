using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tryAGI.Codex;

/// <summary>Explicit account lifecycle operation requiring authenticated host authorization.</summary>
public enum CodexAccountLifecycleOperation
{
    /// <summary>Start a managed ChatGPT device-code login.</summary>
    DeviceLogin,
    /// <summary>Sign out of the currently authorized account.</summary>
    Logout,
}

/// <summary>Host-owned selection fence captured when the operation was authorized.
/// The epoch changes on any account selection, login or logout; revision identifies the credential generation.</summary>
public sealed record CodexAccountLifecycleTarget(
    [property: JsonIgnore] string SessionId,
    [property: JsonIgnore] string SelectionEpoch,
    [property: JsonIgnore] string? CredentialRevision)
{
    /// <inheritdoc />
    public override string ToString() => "CodexAccountLifecycleTarget [redacted]";
}

/// <summary>Host authority for native managed login. Implementations must enforce the active account,
/// allowed login methods, workspace restrictions and durable selection/credential CAS atomically.
/// A false or ambiguous commit must never be automatically retried.</summary>
public interface ICodexAccountLifecycleAuthority
{
    /// <summary>Authorizes the operation and captures its selection fence; may require explicit user consent.</summary>
    Task<CodexAccountLifecycleTarget> AuthorizeAsync(CodexAccountLifecycleOperation operation, CancellationToken cancellationToken);

    /// <summary>Atomically installs OAuth credentials only if the captured fence is still valid.
    /// Return true only after durable commit, including active selection and cache invalidation.</summary>
    Task<bool> TryCommitLoginAsync(CodexAccountLifecycleTarget target, Guid loginId, CodexOAuthTokens tokens, CancellationToken cancellationToken);

    /// <summary>Atomically clears the captured active login and invalidates its authority.
    /// Any required provider revocation and uncertain outcomes belong to this host adapter.</summary>
    Task<bool> TryLogoutAsync(CodexAccountLifecycleTarget target, CancellationToken cancellationToken);
}

/// <summary>Connection-bound native device-code login lifecycle. The embedding App Server must call
/// <see cref="RunPendingAsync"/> once after sending the start response, retain that task and cancel its
/// lifetime on disconnect/shutdown. It does not start browser login, accept arbitrary bearer tokens or select paid API keys.</summary>
public sealed class CodexDeviceLoginRpc
{
    private readonly CodexDeviceAuthorization authorization;
    private readonly ICodexAccountLifecycleAuthority authority;
    private readonly Func<string, JsonElement, Task> notify;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Attempt? active;
    private long notificationFailures;

    private sealed class Attempt(Guid id, CodexAccountLifecycleTarget target, CodexDeviceCode code)
    {
        internal Guid Id { get; } = id;
        internal CodexAccountLifecycleTarget Target { get; } = target;
        internal CodexDeviceCode Code { get; } = code;
        internal CancellationTokenSource Stop { get; } = new();
        internal bool Running { get; set; }
    }

    /// <summary>Creates the connection-bound device login facade.</summary>
    public CodexDeviceLoginRpc(CodexDeviceAuthorization authorization, ICodexAccountLifecycleAuthority authority,
        Func<string, JsonElement, Task> notify, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(notify);
        this.authorization = authorization;
        this.authority = authority;
        this.notify = notify;
        time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of completion notifications not delivered by the host adapter.</summary>
    public long NotificationFailures => Interlocked.Read(ref notificationFailures);

    /// <summary>Whether this facade handles the given lifecycle method.</summary>
    public static bool Handles(string method) => method is "account/login/start" or "account/login/cancel" or "account/logout";

    /// <summary>Admits an explicitly authorized device login, cancels a correlated attempt or logs out the active session.</summary>
    public async Task<JsonElement> InvokeAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!Handles(method)) throw new CodexThreadRpcException(-32601, "Unsupported account lifecycle method.");
        var p = parameters is { ValueKind: not JsonValueKind.Null } supplied ? supplied : Element(new JsonObject());
        if (p.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(p.GetRawText()) > 4096)
            throw Invalid("Invalid account lifecycle parameters.");
        var field = method switch { "account/login/start" => "type", "account/login/cancel" => "loginId", _ => null };
        var count = 0;
        foreach (var item in p.EnumerateObject())
            if (++count > 1 || item.Name != field) throw Invalid("Unknown or duplicate account lifecycle parameter.");

        switch (method)
        {
            case "account/login/start":
                if (!p.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "chatgptDeviceCode")
                    throw Invalid("Only explicitly authorized ChatGPT device-code login is registered.");
                return await StartAsync(cancellationToken).ConfigureAwait(false);
            case "account/login/cancel":
                if (!p.TryGetProperty("loginId", out var id) || id.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(id.GetString(), out var loginId) || loginId == Guid.Empty)
                    throw Invalid("Invalid login ID.");
                return Element(new JsonObject { ["status"] = await CancelAsync(loginId, cancellationToken).ConfigureAwait(false) ? "canceled" : "notFound" });
            default:
                await LogoutAsync(cancellationToken).ConfigureAwait(false);
                return Element(new JsonObject());
        }
    }

    private async Task<JsonElement> StartAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        Attempt? superseded = null;
        try
        {
            var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.DeviceLogin, ct).ConfigureAwait(false);
            Validate(target);
            CodexDeviceCode code;
            try { code = await authorization.BeginAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new CodexProtocolException("Device login could not be started."); }
            ct.ThrowIfCancellationRequested();
            if (code.ExpiresAt <= time.GetUtcNow() || code.PollInterval <= TimeSpan.Zero ||
                code.PollInterval > TimeSpan.FromMinutes(1) || string.IsNullOrWhiteSpace(code.UserCode))
                throw new CodexProtocolException("Invalid device authorization.");
            superseded = TakeActive();
            var attempt = new Attempt(Guid.NewGuid(), target, code);
            active = attempt;
            return Element(new JsonObject
            {
                ["type"] = "chatgptDeviceCode", ["loginId"] = attempt.Id.ToString(),
                ["verificationUrl"] = code.VerificationUri.AbsoluteUri, ["userCode"] = code.UserCode
            });
        }
        finally
        {
            gate.Release();
            if (superseded is not null) await CompletedAsync(superseded.Id, false, "Login was not completed.").ConfigureAwait(false);
        }
    }

    /// <summary>Drives the exact admitted attempt after the host sends its start response.
    /// Duplicate drivers are rejected. A false result means no confirmed login commit.</summary>
    public async Task<bool> RunPendingAsync(Guid loginId, CancellationToken cancellationToken = default)
    {
        Attempt attempt;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is not { } current || current.Id != loginId) return false;
            if (current.Running) throw Invalid("Login attempt already has a driver.");
            current.Running = true;
            attempt = current;
        }
        finally { gate.Release(); }

        var success = false;
        var error = "Login was not completed.";
        var publish = false;
        try
        {
            var remaining = attempt.Code.ExpiresAt - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return false;
            using var expiry = new CancellationTokenSource(remaining, time);
            using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attempt.Stop.Token, expiry.Token);
            var ct = polling.Token;
            CodexOAuthTokens? tokens = null;
            while (tokens is null)
            {
                await Task.Delay(attempt.Code.PollInterval, time, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                tokens = await authorization.PollAsync(attempt.Code, ct).ConfigureAwait(false);
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(active, attempt)) return false;
                if (tokens.ExpiresAt <= time.GetUtcNow() || string.IsNullOrWhiteSpace(tokens.AccessToken) ||
                    string.IsNullOrWhiteSpace(tokens.RefreshToken) || string.IsNullOrWhiteSpace(tokens.AccountId))
                    return false;
                // Serialize local cancel/logout against the commit. Once admitted, settle the durable
                // CAS using its own deadline rather than abandoning a committed login on caller cancellation.
                using var commitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
                try
                {
                    success = await authority.TryCommitLoginAsync(attempt.Target, attempt.Id, tokens, commitDeadline.Token).ConfigureAwait(false);
                    error = success ? "" : "Account selection changed before login committed.";
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                { error = "Login commit outcome is unconfirmed."; }
                active = null;
                publish = true;
            }
            finally { gate.Release(); }
            return success;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        { error = "Device authorization failed."; return false; }
        finally
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(active, attempt)) { active = null; publish = true; }
                attempt.Stop.Dispose();
            }
            finally { gate.Release(); }
            if (publish) await CompletedAsync(attempt.Id, success, success ? null : error).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels the current connection's pending login without revoking another account.</summary>
    public async Task CancelPendingAsync(CancellationToken cancellationToken = default)
    {
        Attempt? canceled;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { canceled = TakeActive(); }
        finally { gate.Release(); }
        if (canceled is not null) await CompletedAsync(canceled.Id, false, "Login was not completed.").ConfigureAwait(false);
    }

    private async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        Attempt? canceled;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { canceled = active?.Id == id ? TakeActive() : null; }
        finally { gate.Release(); }
        if (canceled is null) return false;
        await CompletedAsync(canceled.Id, false, "Login was not completed.").ConfigureAwait(false);
        return true;
    }

    private async Task LogoutAsync(CancellationToken ct)
    {
        Attempt? canceled = null;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = await authority.AuthorizeAsync(CodexAccountLifecycleOperation.Logout, ct).ConfigureAwait(false);
            Validate(target);
            ct.ThrowIfCancellationRequested();
            canceled = TakeActive();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
            bool committed;
            try { committed = await authority.TryLogoutAsync(target, deadline.Token).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { throw new CodexProtocolException("Logout commit outcome is unconfirmed."); }
            if (!committed) throw new CodexOAuthRefreshException(CodexOAuthRefreshFailure.SessionChanged);
        }
        finally
        {
            gate.Release();
            if (canceled is not null) await CompletedAsync(canceled.Id, false, "Login was not completed.").ConfigureAwait(false);
        }
    }

    // Called only under the gate. The driver owns disposal once started.
    private Attempt? TakeActive()
    {
        var attempt = active;
        if (attempt is null) return null;
        active = null;
        try { attempt.Stop.Cancel(); }
        finally { if (!attempt.Running) attempt.Stop.Dispose(); }
        return attempt;
    }

    private async Task CompletedAsync(Guid id, bool success, string? error)
    {
        try
        {
            await notify("account/login/completed", Element(new JsonObject
            {
                ["loginId"] = id.ToString(), ["success"] = success, ["error"] = error, ["onboardingEntrypoint"] = null
            })).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { Interlocked.Increment(ref notificationFailures); }
    }

    private static void Validate(CodexAccountLifecycleTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.SessionId) || string.IsNullOrWhiteSpace(target.SelectionEpoch) ||
            target.SessionId.Length > 512 || target.SelectionEpoch.Length > 512 ||
            target.SessionId.Any(char.IsControl) || target.SelectionEpoch.Any(char.IsControl))
            throw new CodexProtocolException("Invalid account lifecycle authority.");
    }
    private static JsonElement Element(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
    private static CodexThreadRpcException Invalid(string message) => new(-32602, message);
}
