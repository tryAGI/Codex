using System.Text.Json;
using System.Text;

namespace tryAGI.Codex.Tests;

public sealed class CodexReviewRpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("""{"type":"uncommittedChanges"}""", "uncommittedChanges")]
    [InlineData("""{"type":"baseBranch","branch":"main"}""", "baseBranch")]
    [InlineData("""{"type":"commit","sha":"abc","title":"Commit"}""", "commit")]
    [InlineData("""{"type":"custom","instructions":"Review changes"}""", "custom")]
    public async Task ReviewStartRequiresCommittedCorrelatedInlineTurn(string target, string type)
    {
        var f = await Fixture.Create();
        var id = Guid.NewGuid();
        var request = f.Request(",\"target\":" + target + ",\"delivery\":\"inline\"");
        var result = await f.Host.HandleAsync("review/start", request, id, "reviewer", Ct);
        Assert.Equal(f.Thread.ToString(), result.GetProperty("reviewThreadId").GetString());
        Assert.Equal(id.ToString(), result.GetProperty("turn").GetProperty("id").GetString());
        Assert.Equal(type, f.Adapter.Target!.Type);
        var stored = await f.Threads.ReadAsync(f.Thread, Ct);
        Assert.Equal(id, Assert.Single(stored.Turns).Id);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            f.Host.HandleAsync("review/start", request, id, "reviewer", Ct));
        Assert.Equal(1, f.Adapter.Starts);
    }

    [Theory]
    [InlineData("""{"type":"baseBranch","branch":""}""")]
    [InlineData("""{"type":"commit","sha":""}""")]
    [InlineData("""{"type":"custom","instructions":" "}""")]
    [InlineData("""{"type":"uncommittedChanges","branch":"main"}""")]
    [InlineData("""{"type":"unknown"}""")]
    public async Task InvalidTargetsCannotReachAdapter(string target)
    {
        var f = await Fixture.Create();
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host.HandleAsync("review/start",
            f.Request(",\"target\":" + target), Guid.NewGuid(), "reviewer", Ct));
        Assert.Equal(0, f.Adapter.Starts);
    }

    [Fact]
    public async Task SeparateDeliveryAndUnauthenticatedCallerAreRejected()
    {
        var f = await Fixture.Create();
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host.HandleAsync("review/start",
            f.Request(",\"target\":{\"type\":\"uncommittedChanges\"},\"delivery\":\"detached\""),
            Guid.NewGuid(), "reviewer", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Host.HandleAsync("review/start",
            f.Request(",\"target\":{\"type\":\"uncommittedChanges\"}"), Guid.NewGuid(), "model", Ct));
        Assert.Equal(0, f.Adapter.Starts);
    }

    [Fact]
    public async Task MissingJournalTurnCannotBeReportedAsStarted()
    {
        var f = await Fixture.Create();
        f.Adapter.SkipCommit = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Host.HandleAsync("review/start",
            f.Request(",\"target\":{\"type\":\"uncommittedChanges\"}"), Guid.NewGuid(), "reviewer", Ct));
        Assert.Empty((await f.Threads.ReadAsync(f.Thread, Ct)).Turns);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task GuardianAssessmentIsCorrelatedAdvisoryAndDoesNotApprove(string outcome)
    {
        var f = await Fixture.Create();
        f.Adapter.Outcome = outcome;
        var operation = Guid.NewGuid();
        var action = f.Action();
        var pending = await f.Approvals.RequestAsync(Guid.NewGuid(), action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        var result = await f.Host.HandleAsync("thread/guardianReview", f.Request(), operation, "reviewer", Ct);
        Assert.Equal(operation.ToString(), result.GetProperty("callId").GetString());
        Assert.Equal(outcome, result.GetProperty("assessment").GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("futureField").GetBoolean());
        Assert.Null(await f.Approvals.TryConsumeAsync(pending.Id, action, Ct));
    }

    [Fact]
    public async Task NullAssessmentAndUnknownOrForeignAssessmentNeverGrantAuthority()
    {
        var f = await Fixture.Create();
        f.Adapter.SkipAssessment = true;
        var result = await f.Host.HandleAsync("thread/guardianReview", f.Request(), Guid.NewGuid(), "reviewer", Ct);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("assessment").ValueKind);
        f.Adapter.SkipAssessment = false;
        f.Adapter.Outcome = "maybe";
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            f.Host.HandleAsync("thread/guardianReview", f.Request(), Guid.NewGuid(), "reviewer", Ct));
        f.Adapter.Outcome = "allow";
        f.Adapter.WrongCall = true;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            f.Host.HandleAsync("thread/guardianReview", f.Request(), Guid.NewGuid(), "reviewer", Ct));
    }

    [Fact]
    public async Task EscalationRequiresRecordedDenialExactActionAndSeparateConsumption()
    {
        var f = await Fixture.Create();
        var action = f.Action();
        var pending = await f.Approvals.RequestAsync(Guid.NewGuid(), action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        var raw = """{"outcome":"deny","call_id":"review"}""";
        var request = f.Request(",\"event\":" + raw);
        f.Adapter.Escalation = new(CodexApprovalAction.Fingerprint(Encoding.UTF8.GetBytes(raw)), pending.Id, action, pending.Revision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Host.HandleAsync("thread/approveGuardianDeniedAction",
            request, Guid.NewGuid(), "model", Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host.HandleAsync("thread/approveGuardianDeniedAction",
            f.Request(",\"event\":{\"outcome\":\"allow\",\"call_id\":\"review\"}"), Guid.NewGuid(), "reviewer", Ct));
        Assert.Null(await f.Approvals.TryConsumeAsync(pending.Id, action, Ct));
        var response = await f.Host.HandleAsync("thread/approveGuardianDeniedAction", request, Guid.NewGuid(), "reviewer", Ct);
        Assert.Empty(response.EnumerateObject());
        Assert.Equal(CodexApprovalState.Approved, (await f.Approvals.ReadAsync(pending.Id, Ct))!.State);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host.HandleAsync("thread/approveGuardianDeniedAction",
            request, Guid.NewGuid(), "reviewer", Ct));
        Assert.NotNull(await f.Approvals.TryConsumeAsync(pending.Id, action, Ct));
        Assert.Null(await f.Approvals.TryConsumeAsync(pending.Id, action, Ct));
    }

    [Fact]
    public async Task UnknownOrAlreadyDeniedActionCannotBeEscalated()
    {
        var f = await Fixture.Create();
        var action = f.Action();
        var pending = await f.Approvals.RequestAsync(Guid.NewGuid(), action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        var raw = """{"outcome":"deny"}""";
        var request = f.Request(",\"event\":" + raw);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host.HandleAsync("thread/approveGuardianDeniedAction",
            request, Guid.NewGuid(), "reviewer", Ct));
        var denied = await f.Approvals.DecideAsync(pending.Id, action, 1, false, "reviewer", Ct);
        f.Adapter.Escalation = new(CodexApprovalAction.Fingerprint(Encoding.UTF8.GetBytes(raw)), pending.Id, action, denied.Revision);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host.HandleAsync("thread/approveGuardianDeniedAction",
            request, Guid.NewGuid(), "reviewer", Ct));
        Assert.Equal(CodexApprovalState.Denied, (await f.Approvals.ReadAsync(pending.Id, Ct))!.State);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAnyReviewDispatch()
    {
        var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Host.HandleAsync("review/start",
            f.Request(",\"target\":{\"type\":\"uncommittedChanges\"}"), Guid.NewGuid(), "reviewer", cancellation.Token));
        Assert.Equal(0, f.Adapter.Starts);
    }

    private sealed class Fixture
    {
        internal Guid Thread { get; } = Guid.NewGuid();
        internal CodexThreadStore Threads { get; } = new(new ThreadStorage(), "account");
        internal CodexApprovalStore Approvals { get; } = new(new ApprovalStorage(), "account",
            (authorization, _) => authorization.ActorId == "reviewer" ? ValueTask.CompletedTask : throw new UnauthorizedAccessException());
        internal Adapter Adapter { get; }
        internal CodexReviewRpc Host { get; }

        private Fixture()
        {
            Adapter = new(Threads);
            Host = new(Threads, Approvals, "connection", Adapter,
                (context, _) => context.ActorId == "reviewer" ? ValueTask.CompletedTask : throw new UnauthorizedAccessException());
        }

        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Threads.CreateAsync(f.Thread, Json("""{"model":"test"}"""), Ct);
            return f;
        }

        internal JsonElement Request(string extra = "") => Json("{\"threadId\":\"" + Thread + "\"" + extra + "}");
        internal CodexApprovalAction Action() => new(CodexApprovalKind.Command, "connection", "attempt",
            CodexApprovalAction.Fingerprint("resolved action"u8), Thread.ToString(), "turn", "item");
    }

    private sealed class Adapter(CodexThreadStore threads) : ICodexReviewAdapter
    {
        internal int Starts { get; private set; }
        internal CodexReviewTarget? Target { get; private set; }
        internal bool SkipCommit { get; set; }
        internal bool SkipAssessment { get; set; }
        internal bool WrongCall { get; set; }
        internal string Outcome { get; set; } = "deny";
        internal CodexGuardianEscalation? Escalation { get; set; }

        public async Task<JsonElement> StartReviewAsync(CodexReviewContext context, long expectedThreadRevision,
            CodexReviewTarget target, CancellationToken cancellationToken)
        {
            Starts++;
            Target = target;
            if (!SkipCommit)
                await threads.StartTurnAsync(context.ThreadId, expectedThreadRevision, context.OperationId, "host", [],
                    cancellationToken: cancellationToken);
            return Json("{\"id\":\"" + context.OperationId + "\",\"status\":\"inProgress\",\"items\":[]}");
        }

        public Task<JsonElement> ReviewGuardianAsync(CodexReviewContext context, long expectedThreadRevision,
            CancellationToken cancellationToken)
        {
            var call = WrongCall ? Guid.NewGuid() : context.OperationId;
            return Task.FromResult(Json("{\"callId\":\"" + call + "\",\"assessment\":" + (SkipAssessment ? "null" :
                "{\"outcome\":\"" + Outcome + "\",\"riskLevel\":\"low\",\"userAuthorization\":\"unknown\",\"rationale\":\"Synthetic\"}") +
                ",\"futureField\":true}"));
        }

        public Task<CodexGuardianEscalation?> ResolveDeniedActionAsync(CodexReviewContext context,
            CancellationToken cancellationToken) => Task.FromResult(Escalation);
    }

    private sealed class ThreadStorage : ICodexThreadStorage
    {
        private readonly Dictionary<Guid, JsonElement> records = new();
        private readonly object gate = new();
        public Task<JsonElement?> ReadAsync(Guid threadId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(records.TryGetValue(threadId, out var item) ? (JsonElement?)item.Clone() : null);
        }
        public Task<bool> CompareExchangeAsync(Guid threadId, long? expectedRevision, JsonElement document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? revision = records.TryGetValue(threadId, out var old) ? old.GetProperty("revision").GetInt64() : null;
                if (revision != expectedRevision) return Task.FromResult(false);
                records[threadId] = document.Clone();
                return Task.FromResult(true);
            }
        }
    }

    private sealed class ApprovalStorage : ICodexApprovalStorage
    {
        private readonly Dictionary<(string, Guid), CodexApprovalRecord> records = new();
        private readonly object gate = new();
        public Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(records.GetValueOrDefault((accountId, requestId)));
        }
        public Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
            CodexApprovalRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var key = (accountId, requestId);
                if (records.GetValueOrDefault(key)?.Revision != expectedRevision) return Task.FromResult(false);
                records[key] = record;
                return Task.FromResult(true);
            }
        }
    }
}
