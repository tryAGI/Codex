using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexAccountSessionsRpcTests
{
    [Fact]
    public async Task ListsSavedSessionsAndRefreshesMetadataOnlyWhenRequested()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = Create(storage);
        var list = await rpc.InvokeAsync("accountSession/list", cancellationToken: ct);
        Assert.Equal("session-a", list.GetProperty("activeSessionId").GetString());
        Assert.Equal(2, list.GetProperty("sessions").GetArrayLength());
        Assert.True(list.GetProperty("sessions")[0].GetProperty("isActive").GetBoolean());
        Assert.False(list.GetProperty("sessions")[1].GetProperty("isActive").GetBoolean());
        Assert.Equal("personal", list.GetProperty("sessions")[0].GetProperty("workspaces")[0].GetProperty("kind").GetString());
        Assert.Equal(0, storage.Refreshes);
        await rpc.InvokeAsync("accountSession/list", Json("""{"refreshWorkspaceMetadata":true}"""), ct);
        Assert.Equal(1, storage.Refreshes);
        Assert.Equal(0, storage.Writes);
        Assert.DoesNotContain(storage.State.SelectionEpoch, list.GetRawText());
        Assert.DoesNotContain(storage.Credential.VaultVersion, list.GetRawText());
    }

    [Fact]
    public async Task SwitchCommitsExactCredentialFenceAndChangesEpochEvenWhenSwitchingBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var original = storage.State;
        var rpc = Create(storage);
        var selected = await Switch(rpc, "session-b", "workspace-b", ct);
        Assert.Equal("session-b", selected.GetProperty("activeSessionId").GetString());
        Assert.Equal("workspace-b", selected.GetProperty("sessions")[1].GetProperty("selectedWorkspaceAccountId").GetString());
        Assert.NotEqual(original.Revision, storage.State.Revision);
        Assert.NotEqual(original.SelectionEpoch, storage.State.SelectionEpoch);
        var changed = storage.State.SelectionEpoch;
        Assert.Equal(1, storage.Writes);
        // A new facade observes the persisted selection, not a process-local cache.
        rpc = Create(storage);
        var reopened = await rpc.InvokeAsync("accountSession/list", cancellationToken: ct);
        Assert.Equal("session-b", reopened.GetProperty("activeSessionId").GetString());
        await Switch(rpc, "session-b", "workspace-b", ct);
        Assert.Equal(changed, storage.State.SelectionEpoch);
        storage.Credential = storage.Credential with
        { SessionId = "session-a", AccountId = "workspace-a", UserId = "user-a" };
        await Switch(rpc, "session-a", "workspace-a", ct);
        Assert.NotEqual(original.SelectionEpoch, storage.State.SelectionEpoch);
        Assert.NotEqual(changed, storage.State.SelectionEpoch);
        Assert.Equal("session-a", storage.State.ActiveSessionId);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("expired")]
    [InlineData("restricted")]
    [InlineData("wrong-user")]
    [InlineData("wrong-workspace")]
    [InlineData("wrong-session")]
    [InlineData("missing")]
    public async Task UnusableCredentialNeverChangesSelectionOrStartsBrowserLogin(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        storage.Credential = reason switch
        {
            "pending" => storage.Credential with { RefreshPending = true },
            "expired" => storage.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) },
            "restricted" => storage.Credential with { Restricted = true },
            "wrong-user" => storage.Credential with { UserId = "other" },
            "wrong-workspace" => storage.Credential with { AccountId = "other" },
            "wrong-session" => storage.Credential with { SessionId = "other" },
            _ => storage.Credential
        };
        storage.MissingCredential = reason == "missing";
        var rpc = Create(storage);
        var before = storage.State;
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("accountSession/switch",
            Json("""{"sessionId":"session-b","accountId":"workspace-b","allowBrowserLogin":true}"""), ct));
        Assert.Same(before, storage.State);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task AuthorizationDenialHappensBeforeDirectoryRead()
    {
        var storage = new Storage();
        var rpc = new CodexAccountSessionsRpc(storage, (_, _, _) => throw new CodexAuthorizationUnavailableException());
        await Assert.ThrowsAsync<CodexAuthorizationUnavailableException>(() => Switch(rpc, "session-b", "workspace-b", TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.Reads);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task DirectoryAndCredentialRacesAreNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = Create(storage);
        storage.BeforeCommit = () => storage.State = storage.State with { Revision = "concurrent" };
        var error = await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => Switch(rpc, "session-b", "workspace-b", ct));
        Assert.Equal(CodexOAuthRefreshFailure.SessionChanged, error.Failure);
        Assert.Equal(1, storage.Writes);
        Assert.Equal("session-a", storage.State.ActiveSessionId);
        storage.BeforeCommit = () => storage.Credential = storage.Credential with { VaultVersion = "rotation-pending", RefreshPending = true };
        await Assert.ThrowsAsync<CodexOAuthRefreshException>(() => Switch(rpc, "session-b", "workspace-b", ct));
        Assert.Equal(2, storage.Writes);
        Assert.Equal("session-a", storage.State.ActiveSessionId);
    }

    [Fact]
    public async Task AmbiguousCommitIsReportedWithoutAutomaticReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage { LoseAcknowledgement = true };
        var rpc = Create(storage);
        var error = await Assert.ThrowsAsync<CodexThreadRpcException>(() => Switch(rpc, "session-b", "workspace-b", ct));
        Assert.Contains("unconfirmed", error.Message);
        Assert.Equal(1, storage.Writes);
        Assert.Equal("session-b", storage.State.ActiveSessionId);
        var list = await rpc.InvokeAsync("accountSession/list", cancellationToken: ct);
        Assert.Equal("session-b", list.GetProperty("activeSessionId").GetString());
    }

    [Theory]
    [InlineData("{\"sessionId\":\"session-b\",\"accountId\":\"workspace-b\",\"sessionId\":\"other\"}")]
    [InlineData("{\"sessionId\":\"session-b\",\"accountId\":123}")]
    [InlineData("{\"sessionId\":\"session-b\",\"accountId\":\"workspace-b\",\"allowBrowserLogin\":\"true\"}")]
    public async Task InvalidParametersNeverReachStorage(string json)
    {
        var storage = new Storage();
        var rpc = Create(storage);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("accountSession/switch", Json(json), TestContext.Current.CancellationToken));
        Assert.Equal(0, storage.Reads);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task UnknownWorkspaceAndMalformedDirectoryFailClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var rpc = Create(storage);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Switch(rpc, "session-b", "unknown", ct));
        Assert.Equal(0, storage.CredentialReads);
        storage.State = storage.State with { ActiveSessionId = "missing" };
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => rpc.InvokeAsync("accountSession/list", cancellationToken: ct));
        Assert.Equal(0, storage.Writes);
    }

    private static CodexAccountSessionsRpc Create(Storage storage) =>
        new(storage, (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

    private static Task<JsonElement> Switch(CodexAccountSessionsRpc rpc, string id, string account, CancellationToken ct) =>
        rpc.InvokeAsync("accountSession/switch", Json($$"""{"sessionId":"{{id}}","accountId":"{{account}}"}"""), ct);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class Storage : ICodexAccountSessionDirectoryStorage
    {
        internal CodexAccountSessionDirectorySnapshot State { get; set; } = new("revision-a", "epoch-a", "session-a",
        [
            new("session-a", "user-a", "a@example.test", "A", null, 1, "workspace-a",
                [new("workspace-a", "Personal", null, "personal")]),
            new("session-b", "user-b", null, "B", null, 2, "workspace-b",
                [new("workspace-b", "Workspace", null, "workspace")])
        ]);
        internal CodexAccountSessionCredentialFence Credential { get; set; } =
            new("vault-version", "session-b", "workspace-b", "user-b", DateTimeOffset.UtcNow.AddHours(1), false, false);
        internal int Reads { get; private set; }
        internal int Refreshes { get; private set; }
        internal int CredentialReads { get; private set; }
        internal int Writes { get; private set; }
        internal bool MissingCredential { get; set; }
        internal bool LoseAcknowledgement { get; init; }
        internal Action? BeforeCommit { get; set; }

        public Task<CodexAccountSessionDirectorySnapshot> ReadAsync(bool refreshWorkspaceMetadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (refreshWorkspaceMetadata) Refreshes++;
            return Task.FromResult(State);
        }
        public Task<CodexAccountSessionCredentialFence?> ReadCredentialFenceAsync(string sessionId, string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialReads++;
            return Task.FromResult<CodexAccountSessionCredentialFence?>(MissingCredential ? null : Credential);
        }
        public Task<bool> TryCommitSelectionAsync(string expectedRevision, CodexAccountSessionCredentialFence credential,
            CodexAccountSessionDirectorySnapshot replacement, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            BeforeCommit?.Invoke();
            if (State.Revision != expectedRevision || credential != Credential) return Task.FromResult(false);
            State = replacement;
            if (LoseAcknowledgement) throw new IOException("secret internal data");
            return Task.FromResult(true);
        }
    }
}
