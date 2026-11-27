using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexApprovalRpcBridgeTests
{
    private const string Command = "item/commandExecution/requestApproval";
    private const string File = "item/fileChange/requestApproval";
    private const string Permissions = "item/permissions/requestApproval";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
    private static JsonElement Request(string extra = "") =>
        Json("""{"threadId":"thread","turnId":"turn","itemId":"item","startedAtMs":1""" + extra + "}");
    private static CodexApprovalAction Action(CodexApprovalKind kind = CodexApprovalKind.Command, string owner = "connection") =>
        new(kind, owner, "attempt", CodexApprovalAction.Fingerprint("exact resolved action"u8), "thread", "turn", "item");

    [Fact]
    public async Task NumericAndStringIdsDoNotAliasAndExactDuplicateAdmitsOnce()
    {
        var f = new Fixture();
        var action = Action();
        var numeric = await f.Admit(Json("7"), Command, Request(), action);
        Assert.True(numeric.Created);
        var duplicate = await f.NewBridge().AdmitAsync(Json("7"), Command, Request(), action, f.Expires, Ct);
        Assert.False(duplicate.Created);
        Assert.Equal(numeric.Record, duplicate.Record);
        var text = await f.Admit(Json("\"7\""), Command, Request(), action with { ActionId = "other-attempt" });
        Assert.True(text.Created);
        Assert.NotEqual(numeric.Record.Id, text.Record.Id);
        Assert.True(numeric.Record.RpcBinding!.RequestId.IsNumeric);
        Assert.False(text.Record.RpcBinding!.RequestId.IsNumeric);
    }

    [Fact]
    public async Task ChangedPayloadMethodDeadlineAndActionCannotRebindTransportIdentity()
    {
        var f = new Fixture();
        var id = Json("3");
        var action = Action();
        await f.Admit(id, Command, Request(",\"command\":\"first\""), action);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Admit(id, Command, Request(",\"command\":\"second\""), action));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Admit(id, File, Request(), Action(CodexApprovalKind.FileChange)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.NewBridge().AdmitAsync(id, Command, Request(",\"command\":\"first\""), action, f.Expires.AddSeconds(1), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Admit(id, Command, Request(",\"command\":\"first\""), action with { ActionId = "changed" }));
    }

    [Fact]
    public async Task ExactReviewerResponseCommitsButOnlyStoreConsumptionAdmitsDispatch()
    {
        var f = new Fixture();
        var id = Json("9");
        var action = Action();
        var admission = await f.Admit(id, Command, Request(",\"availableDecisions\":[\"accept\",\"decline\"]"), action);
        Assert.Null(await f.Store.TryConsumeAsync(admission.Record.Id, action, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Bridge.RespondAsync(id, Command, action, 1,
            "model", Json("""{"decision":"accept"}"""), Ct));
        var accepted = await f.NewBridge().RespondAsync(id, Command, action, 1, "reviewer",
            Json("""{"decision":"accept"}"""), Ct);
        Assert.Equal(CodexApprovalRpcDisposition.Applied, accepted.Disposition);
        Assert.Equal(CodexApprovalState.Approved, accepted.Record.State);
        Assert.Equal(2, accepted.Record.Revision);
        var duplicate = await f.NewBridge().RespondAsync(id, Command, action, 1, "reviewer",
            Json("""{"decision":"accept"}"""), Ct);
        Assert.Equal(CodexApprovalRpcDisposition.AlreadyDecided, duplicate.Disposition);
        Assert.Equal(2, duplicate.Record.Revision);
        var consumed = await f.Store.TryConsumeAsync(accepted.Record.Id, action, Ct);
        Assert.Equal(CodexApprovalState.Consumed, consumed!.State);
        Assert.Null(await f.Store.TryConsumeAsync(accepted.Record.Id, action, Ct));
    }

    [Fact]
    public async Task CrossConnectionAccountAndChangedActionCannotAnswer()
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action();
        var request = await f.Admit(id, Command, Request(), action);
        var response = Json("""{"decision":"accept"}""");
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.NewBridge("foreign").RespondAsync(id, Command,
            action with { ConnectionId = "foreign" }, 1, "reviewer", response, Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.NewBridge(account: "other-account").RespondAsync(id,
            Command, action, 1, "reviewer", response, Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Bridge.RespondAsync(id, Command,
            action with { ActionSha256 = CodexApprovalAction.Fingerprint("different"u8) }, 1, "reviewer", response, Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Bridge.RespondAsync(id, Command, action, 2,
            "reviewer", response, Ct));
        Assert.Equal(CodexApprovalState.Pending, (await f.Store.ReadAsync(request.Record.Id, Ct))!.State);
    }

    [Theory]
    [InlineData("acceptForSession")]
    [InlineData("acceptWithExecpolicyAmendment")]
    [InlineData("applyNetworkPolicyAmendment")]
    public async Task BroaderCommandDecisionsAreNotTranslatedIntoAccept(string variant)
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action();
        var request = await f.Admit(id, Command, Request(), action);
        var response = variant == "acceptForSession" ? Json("""{"decision":"acceptForSession"}""") :
            Json("{\"decision\":{\"" + variant + "\":{}}}");
        var result = await f.Bridge.RespondAsync(id, Command, action, 1, "reviewer", response, Ct);
        Assert.Equal(CodexApprovalRpcDisposition.Unsupported, result.Disposition);
        Assert.Equal(response.GetRawText(), result.Response.GetRawText());
        Assert.Equal(CodexApprovalState.Pending, result.Record.State);
        Assert.Null(await f.Store.TryConsumeAsync(request.Record.Id, action, Ct));
    }

    [Fact]
    public async Task OfferedChoicesAreEnforcedAndUnknownDecisionsFailClosed()
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action();
        var request = await f.Admit(id, Command, Request(",\"availableDecisions\":[\"decline\"]"), action);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Bridge.RespondAsync(id, Command, action, 1,
            "reviewer", Json("""{"decision":"accept"}"""), Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Bridge.RespondAsync(id, Command, action, 1,
            "reviewer", Json("""{"decision":"unknown"}"""), Ct));
        var denied = await f.Bridge.RespondAsync(id, Command, action, 1, "reviewer",
            Json("""{"decision":"decline"}"""), Ct);
        Assert.Equal(CodexApprovalState.Denied, denied.Record.State);
        Assert.Null(await f.Store.TryConsumeAsync(request.Record.Id, action, Ct));
    }

    [Fact]
    public async Task FileApprovalCancellationDoesNotGrantWrites()
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action(CodexApprovalKind.FileChange);
        var request = await f.Admit(id, File, Request(",\"grantRoot\":\"/somewhere\""), action);
        var result = await f.Bridge.RespondAsync(id, File, action, 1, "reviewer", Json("""{"decision":"cancel"}"""), Ct);
        Assert.Equal(CodexApprovalState.Cancelled, result.Record.State);
        Assert.Null(await f.Store.TryConsumeAsync(request.Record.Id, action, Ct));
    }

    [Fact]
    public async Task PermissionGrantNeedsSeparateEnforcementAndEmptyTurnGrantDenies()
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action(CodexApprovalKind.Permissions);
        var request = await f.Admit(id, Permissions, Request(",\"permissions\":{\"network\":{\"enabled\":true}}"), action);
        var broader = await f.Bridge.RespondAsync(id, Permissions, action, 1, "reviewer",
            Json("""{"permissions":{"network":{"enabled":true}},"scope":"session"}"""), Ct);
        Assert.Equal(CodexApprovalRpcDisposition.Unsupported, broader.Disposition);
        Assert.Equal(CodexApprovalState.Pending, broader.Record.State);
        Assert.Null(await f.Store.TryConsumeAsync(request.Record.Id, action, Ct));
        var denial = await f.Bridge.RespondAsync(id, Permissions, action, 1, "reviewer",
            Json("""{"permissions":{},"scope":"turn"}"""), Ct);
        Assert.Equal(CodexApprovalState.Denied, denial.Record.State);
    }

    [Fact]
    public async Task DisconnectCancellationAndExpiredRequestsCannotBeRevived()
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action();
        await f.Admit(id, Command, Request(), action);
        var cancelled = await f.NewBridge().CancelAsync(id, Command, action, 1, "reviewer", Ct);
        Assert.Equal(CodexApprovalState.Cancelled, cancelled.State);
        var duplicate = await f.NewBridge().RespondAsync(id, Command, action, 1, "reviewer",
            Json("""{"decision":"accept"}"""), Ct);
        Assert.Equal(CodexApprovalRpcDisposition.AlreadyDecided, duplicate.Disposition);

        var second = Json("2");
        var next = action with { ActionId = "next" };
        var request = await f.Admit(second, Command, Request(), next);
        f.Clock.Now = f.Expires;
        var expired = await f.NewBridge().RespondAsync(second, Command, next, 1, "reviewer",
            Json("""{"decision":"accept"}"""), Ct);
        Assert.Equal(CodexApprovalState.Expired, expired.Record.State);
        Assert.Null(await f.Store.TryConsumeAsync(request.Record.Id, next, Ct));
    }

    [Fact]
    public async Task AmbiguousRegistrationDoesNotAuthorizeRepublishing()
    {
        var f = new Fixture();
        var id = Json("1");
        var action = Action();
        f.Storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => f.Admit(id, Command, Request(), action));
        var recovered = await f.NewBridge().AdmitAsync(id, Command, Request(), action, f.Expires, Ct);
        Assert.False(recovered.Created);
        Assert.Equal(CodexApprovalState.Pending, recovered.Record.State);
        Assert.Null(await f.Store.TryConsumeAsync(recovered.Record.Id, action, Ct));
    }

    [Fact]
    public async Task InvalidRequestCorrelationIsRejectedBeforeStorage()
    {
        var f = new Fixture();
        var id = Json("1");
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Admit(id, Command, Request(), Action(owner: "foreign")));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Admit(id, Command, Request(), Action() with { ItemId = "other" }));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Admit(id, Command, Request(",\"kind\":\"future\""), Action()));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Admit(id, Command, Request(",\"environmentId\":\"foreign\""), Action()));
        Assert.Empty(f.Storage.Records);
    }

    private sealed class Fixture
    {
        internal Clock Clock { get; } = new();
        internal Storage Storage { get; } = new();
        internal CodexApprovalStore Store { get; }
        internal CodexApprovalRpcBridge Bridge { get; }
        internal DateTimeOffset Expires { get; }

        internal Fixture()
        {
            Expires = Clock.Now.AddMinutes(5);
            Store = NewStore("account");
            Bridge = new(Store, "connection");
        }

        private CodexApprovalStore NewStore(string account) => new(Storage, account, (authorization, _) =>
        {
            if (authorization.ActorId != "reviewer") throw new UnauthorizedAccessException();
            return ValueTask.CompletedTask;
        }, Clock);

        internal CodexApprovalRpcBridge NewBridge(string connection = "connection", string account = "account") =>
            new(NewStore(account), connection);

        internal Task<CodexApprovalRpcAdmission> Admit(JsonElement id, string method, JsonElement parameters, CodexApprovalAction action) =>
            Bridge.AdmitAsync(id, method, parameters, action, Expires, Ct);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Storage : ICodexApprovalStorage
    {
        internal Dictionary<(string, Guid), CodexApprovalRecord> Records { get; } = new();
        internal bool ThrowAfterCommit { get; set; }
        private readonly object gate = new();

        public Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(Records.GetValueOrDefault((accountId, requestId)));
        }

        public Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
            CodexApprovalRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var key = (accountId, requestId);
                if (Records.GetValueOrDefault(key)?.Revision != expectedRevision) return Task.FromResult(false);
                Records[key] = record;
                if (ThrowAfterCommit)
                {
                    ThrowAfterCommit = false;
                    throw new IOException("Synthetic uncertain commit.");
                }
                return Task.FromResult(true);
            }
        }
    }
}
