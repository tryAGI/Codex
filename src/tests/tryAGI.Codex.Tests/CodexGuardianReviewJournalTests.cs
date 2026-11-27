using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexGuardianReviewJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task StartAndCompletionAreDurableBeforeOrderedNotificationsAndReplayIsHarmless()
    {
        var f = new Fixture();
        var started = f.Payload("inProgress");
        var completed = f.Payload("approved");
        var start = await f.Journal.StartAsync(started, f.Action, Ct);
        Assert.True(start.Applied);
        Assert.True(start.NotificationConfirmed);
        Assert.Equal(2, start.Record.Revision);
        var nextHost = f.NewJournal();
        var repeatedStart = await nextHost.StartAsync(started, f.Action, Ct);
        Assert.False(repeatedStart.Applied);
        var finish = await nextHost.CompleteAsync(completed, f.Action, cancellationToken: Ct);
        Assert.True(finish.Applied);
        Assert.True(finish.NotificationConfirmed);
        Assert.Equal("approved", finish.Record.Status);
        Assert.Equal(4, finish.Record.Revision);
        Assert.True(finish.Record.CompletedConfirmed);
        Assert.False((await f.NewJournal().CompleteAsync(completed, f.Action, cancellationToken: Ct)).Applied);
        Assert.Equal(["item/autoApprovalReview/started", "item/autoApprovalReview/completed"], f.Notices.Select(x => x.Method));
        Assert.True(f.Notices.Last().Body.GetProperty("futureExtension").GetBoolean());
        Assert.Empty(f.ApprovalStorage.Records);
        var persisted = await f.NewJournal().ReadAsync("review", Ct);
        Assert.Equal("approved", persisted!.Status);
        Assert.Equal(4, persisted.Revision);
    }

    [Theory]
    [InlineData("timedOut")]
    [InlineData("aborted")]
    public async Task TimeoutAndAbortAreTerminalButNeverApproval(string status)
    {
        var f = new Fixture();
        var pending = await f.Approvals.RequestAsync(Guid.NewGuid(), f.Action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        await f.Journal.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        var result = await f.Journal.CompleteAsync(f.Payload(status), f.Action, cancellationToken: Ct);
        Assert.Equal(status, result.Record.Status);
        Assert.True(result.NotificationConfirmed);
        Assert.Null(await f.Approvals.TryConsumeAsync(pending.Id, f.Action, Ct));
        Assert.Null(await f.Journal.BindDeniedEscalationAsync("review", f.Thread, f.Denial(), pending.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Journal.CompleteAsync(f.Payload("approved"), f.Action, cancellationToken: Ct));
    }

    [Fact]
    public async Task CompleteBeforeStartAndConflictingScopesCannotPublish()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Journal.CompleteAsync(f.Payload("denied"), f.Action, cancellationToken: Ct));
        Assert.Empty(f.Notices);
        await f.Journal.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        var changed = f.Action with { ActionSha256 = CodexApprovalAction.Fingerprint("different action"u8) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Journal.StartAsync(f.Payload("inProgress"), changed, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Journal.CompleteAsync(f.Payload("denied"), changed, cancellationToken: Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            f.Journal.CompleteAsync(f.Payload("maybe"), f.Action, cancellationToken: Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            f.Journal.CompleteAsync(f.Payload("denied", completedAt: 0), f.Action, cancellationToken: Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            f.Journal.CompleteAsync(f.Payload("denied", decisionSource: "unknown"), f.Action, cancellationToken: Ct));
        Assert.Single(f.Notices);
        Assert.Equal("inProgress", (await f.Journal.ReadAsync("review", Ct))!.Status);
    }

    [Fact]
    public async Task DenialEvidenceAndPendingApprovalAreBoundExactlyOnce()
    {
        var f = new Fixture();
        var evidence = f.Denial();
        var pending = await f.Approvals.RequestAsync(Guid.NewGuid(), f.Action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        var second = await f.Approvals.RequestAsync(Guid.NewGuid(), f.Action, DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        await f.Journal.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            f.Journal.CompleteAsync(f.Payload("denied"), f.Action, f.Denial(command: "changed"), Ct));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            f.Journal.CompleteAsync(f.Payload("denied"), f.Action, f.Denial(reviewId: "foreign"), Ct));
        var denied = await f.Journal.CompleteAsync(f.Payload("denied"), f.Action, evidence, Ct);
        Assert.True(denied.NotificationConfirmed);
        Assert.Null(await f.Journal.BindDeniedEscalationAsync("review", Guid.NewGuid(), evidence, pending.Id, Ct));
        Assert.Null(await f.Journal.BindDeniedEscalationAsync("review", f.Thread, f.Denial(command: "changed"), pending.Id, Ct));

        var bound = await f.NewJournal().BindDeniedEscalationAsync("review", f.Thread, evidence, pending.Id, Ct);
        Assert.NotNull(bound);
        Assert.Equal(pending.Id, bound.ApprovalId);
        Assert.Equal(f.Action, bound.Action);
        Assert.Equal(pending.Revision, bound.ExpectedRevision);
        Assert.Equal(pending.Id, (await f.NewJournal().ReadAsync("review", Ct))!.EscalationApprovalId);
        Assert.Null(await f.Journal.BindDeniedEscalationAsync("review", f.Thread, evidence, second.Id, Ct));
        Assert.Null(await f.Approvals.TryConsumeAsync(pending.Id, f.Action, Ct));
        await f.Approvals.DecideAsync(bound.ApprovalId, bound.Action, bound.ExpectedRevision, true, "reviewer", Ct);
        Assert.NotNull(await f.Approvals.TryConsumeAsync(pending.Id, f.Action, Ct));
        Assert.Null(await f.Approvals.TryConsumeAsync(pending.Id, f.Action, Ct));
        Assert.Null(await f.NewJournal().BindDeniedEscalationAsync("review", f.Thread, evidence, pending.Id, Ct));
    }

    [Fact]
    public async Task ACommittedButUncertainStartCannotBeReplayedOrOvertaken()
    {
        var f = new Fixture();
        f.Storage.ThrowAfterCommit = true;
        await Assert.ThrowsAsync<IOException>(() => f.Journal.StartAsync(f.Payload("inProgress"), f.Action, Ct));
        Assert.Empty(f.Notices);
        var recovered = f.NewJournal();
        var duplicate = await recovered.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        Assert.False(duplicate.Applied);
        Assert.False(duplicate.NotificationConfirmed);
        var terminal = await recovered.CompleteAsync(f.Payload("aborted"), f.Action, cancellationToken: Ct);
        Assert.True(terminal.Applied);
        Assert.False(terminal.NotificationConfirmed);
        Assert.Equal("aborted", (await recovered.ReadAsync("review", Ct))!.Status);
        Assert.Empty(f.Notices);
    }

    [Fact]
    public async Task ConcurrentCompletionRetainsDecisionWithoutOvertakingInFlightStart()
    {
        var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.NewJournal(async (method, body, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            f.Notices.Enqueue(new(method, body.Clone()));
        });
        var starting = first.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        await entered.Task.WaitAsync(Ct);
        var completed = await f.NewJournal().CompleteAsync(f.Payload("denied"), f.Action, f.Denial(), Ct);
        Assert.True(completed.Applied);
        Assert.False(completed.NotificationConfirmed);
        Assert.Empty(f.Notices);
        release.TrySetResult();
        Assert.True((await starting).NotificationConfirmed);
        var stored = (await f.Journal.ReadAsync("review", Ct))!;
        Assert.True(stored.StartedConfirmed);
        Assert.False(stored.CompletedConfirmed);
        Assert.Equal("denied", stored.Status);
        Assert.Equal("item/autoApprovalReview/started", Assert.Single(f.Notices).Method);
        Assert.False((await f.Journal.CompleteAsync(f.Payload("denied"), f.Action, f.Denial(), Ct)).Applied);
        Assert.Single(f.Notices);
    }

    [Fact]
    public async Task FailedTerminalDeliveryDoesNotReplayOrDiscardDecision()
    {
        var f = new Fixture();
        var journal = f.NewJournal((method, body, _) =>
        {
            if (method == "item/autoApprovalReview/completed") throw new IOException("Uncertain terminal delivery.");
            f.Notices.Enqueue(new(method, body.Clone()));
            return ValueTask.CompletedTask;
        });
        await journal.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        await Assert.ThrowsAsync<IOException>(() =>
            journal.CompleteAsync(f.Payload("denied"), f.Action, f.Denial(), Ct));
        var recovered = f.NewJournal();
        var duplicate = await recovered.CompleteAsync(f.Payload("denied"), f.Action, f.Denial(), Ct);
        Assert.False(duplicate.Applied);
        Assert.False(duplicate.NotificationConfirmed);
        Assert.Equal("denied", duplicate.Record.Status);
        Assert.Single(f.Notices);
    }

    [Fact]
    public async Task CrossAccountConnectionAndDeniedHostScopeCannotReadOrMutate()
    {
        var f = new Fixture();
        await f.Journal.StartAsync(f.Payload("inProgress"), f.Action, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.NewJournal(connection: "foreign").ReadAsync("review", Ct));
        Assert.Null(await f.NewJournal(account: "other-account").ReadAsync("review", Ct));
        f.Denied = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Journal.ReadAsync("review", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Journal.CompleteAsync(f.Payload("approved"), f.Action, cancellationToken: Ct));
        Assert.Single(f.Notices);
    }

    [Theory]
    [InlineData("""{"type":"execve","source":"unifiedExec","program":"/bin/true","argv":[],"cwd":"/workspace"}""", CodexApprovalKind.Command)]
    [InlineData("""{"type":"writeStdin","approvalId":"approval","processId":"process","stdin":"input","cwd":"/workspace"}""", CodexApprovalKind.Command)]
    [InlineData("""{"type":"applyPatch","cwd":"/workspace","files":["file.cs"]}""", CodexApprovalKind.FileChange)]
    [InlineData("""{"type":"networkAccess","target":"example","host":"example.test","protocol":"https","port":443}""", CodexApprovalKind.Permissions)]
    [InlineData("""{"type":"mcpToolCall","server":"server","toolName":"tool"}""", CodexApprovalKind.Tool)]
    [InlineData("""{"type":"requestPermissions","permissions":{}}""", CodexApprovalKind.Permissions)]
    public async Task PinnedActionVariantsPreserveWireData(string raw, CodexApprovalKind kind)
    {
        var f = new Fixture();
        var wire = Json(raw);
        var network = wire.GetProperty("type").GetString() == "networkAccess";
        var action = f.Action with { Kind = kind };
        var payload = f.Payload("inProgress", wire: wire, targetItem: network ? null : "item");
        var started = await f.Journal.StartAsync(payload, action, Ct);
        Assert.True(JsonElement.DeepEquals(wire, started.Record.Started.GetProperty("action")));
        Assert.True(started.NotificationConfirmed);
    }

    private sealed record Notice(string Method, JsonElement Body);

    private sealed class Fixture
    {
        internal Guid Thread { get; } = Guid.NewGuid();
        internal CodexApprovalAction Action { get; }
        internal Storage Storage { get; } = new();
        internal ApprovalMemory ApprovalStorage { get; } = new();
        internal CodexApprovalStore Approvals { get; }
        internal CodexGuardianReviewJournal Journal { get; }
        internal ConcurrentQueue<Notice> Notices { get; } = new();
        internal bool Denied { get; set; }

        internal Fixture()
        {
            Action = new(CodexApprovalKind.Command, "connection", "attempt",
                CodexApprovalAction.Fingerprint("resolved exact action"u8), Thread.ToString(), "turn", "item");
            Approvals = NewApprovals("account");
            Journal = NewJournal();
        }

        private CodexApprovalStore NewApprovals(string account) => new(ApprovalStorage, account,
            (decision, _) => decision.ActorId == "reviewer" ? ValueTask.CompletedTask : throw new UnauthorizedAccessException());

        internal CodexGuardianReviewJournal NewJournal(
            Func<string, JsonElement, CancellationToken, ValueTask>? notify = null,
            string connection = "connection", string account = "account") =>
            new(Storage, account == "account" ? Approvals : NewApprovals(account), connection,
                (_, _) => Denied ? throw new UnauthorizedAccessException() : ValueTask.CompletedTask,
                notify ?? (async (method, body, ct) =>
                {
                    var stored = await Storage.ReadAsync(account, body.GetProperty("reviewId").GetString()!, ct);
                    Assert.NotNull(stored);
                    if (method.EndsWith("/completed", StringComparison.Ordinal)) Assert.NotNull(stored.Completed);
                    Notices.Enqueue(new(method, body.Clone()));
                }));

        internal JsonElement Payload(string status, JsonElement? wire = null, string? targetItem = "item",
            long completedAt = 2, string decisionSource = "agent")
        {
            var body = new JsonObject
            {
                ["threadId"] = Thread.ToString(),
                ["turnId"] = "turn",
                ["reviewId"] = "review",
                ["targetItemId"] = targetItem,
                ["startedAtMs"] = 1,
                ["review"] = new JsonObject { ["status"] = status, ["riskLevel"] = "low", ["userAuthorization"] = "unknown" },
                ["action"] = JsonNode.Parse((wire ?? Json("""{"type":"command","source":"shell","command":"test","cwd":"/workspace"}""")).GetRawText()),
                ["futureExtension"] = true,
            };
            if (status != "inProgress")
            {
                body["completedAtMs"] = completedAt;
                body["decisionSource"] = decisionSource;
            }
            return JsonSerializer.SerializeToElement(body);
        }

        internal JsonElement Denial(string reviewId = "review", string command = "test") => JsonSerializer.SerializeToElement(new
        {
            id = reviewId,
            turn_id = "turn",
            target_item_id = "item",
            started_at_ms = 1,
            completed_at_ms = 2,
            status = "denied",
            decision_source = "agent",
            action = new { type = "command", source = "shell", command, cwd = "/workspace" },
        });
    }

    private sealed class Storage : ICodexGuardianReviewStorage
    {
        private readonly Dictionary<(string, string), string> records = new();
        private readonly object gate = new();
        internal bool ThrowAfterCommit { get; set; }

        public Task<CodexGuardianReviewRecord?> ReadAsync(string accountId, string reviewId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
                return Task.FromResult(records.TryGetValue((accountId, reviewId), out var json)
                    ? JsonSerializer.Deserialize(json, CodexGuardianReviewJson.Default.CodexGuardianReviewRecord) : null);
        }

        public Task<bool> CompareExchangeAsync(string accountId, string reviewId, long? expectedRevision,
            CodexGuardianReviewRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var key = (accountId, reviewId);
                var current = records.TryGetValue(key, out var json)
                    ? JsonSerializer.Deserialize(json, CodexGuardianReviewJson.Default.CodexGuardianReviewRecord) : null;
                if (current?.Revision != expectedRevision) return Task.FromResult(false);
                records[key] = JsonSerializer.Serialize(record, CodexGuardianReviewJson.Default.CodexGuardianReviewRecord);
                if (ThrowAfterCommit)
                {
                    ThrowAfterCommit = false;
                    throw new IOException("Synthetic ambiguous commit.");
                }
                return Task.FromResult(true);
            }
        }
    }

    private sealed class ApprovalMemory : ICodexApprovalStorage
    {
        internal Dictionary<(string, Guid), CodexApprovalRecord> Records { get; } = new();
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
                return Task.FromResult(true);
            }
        }
    }
}
