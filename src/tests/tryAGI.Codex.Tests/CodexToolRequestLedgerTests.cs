using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexToolRequestLedgerTests
{
    private sealed class Storage : ICodexToolRequestStorage
    {
        private readonly Dictionary<CodexToolRequestKey, CodexToolRequestRecord> records = [];
        public Task<CodexToolRequestRecord?> ReadAsync(CodexToolRequestKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (records) return Task.FromResult(records.GetValueOrDefault(key));
        }

        public Task<bool> CompareExchangeAsync(CodexToolRequestKey key, long? expectedRevision,
            CodexToolRequestRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (records)
            {
                if (records.GetValueOrDefault(key)?.Revision != expectedRevision) return Task.FromResult(false);
                records[key] = record;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly CodexToolRequestKey Key = new("account", "connection", new("1", true));
    private static readonly CodexToolRequestCorrelation Dynamic = new(Key, CodexToolRequestKind.DynamicTool, "thread", "turn", "call");
    private static readonly CodexToolRequestCorrelation Input = Dynamic with { Kind = CodexToolRequestKind.UserInput, ItemId = "item" };
    private static readonly CodexToolRequestCorrelation Mcp = new(Key, CodexToolRequestKind.McpElicitation, "thread",
        ServerName: "server", EnvironmentId: "env");
    private static JsonElement DynamicParameters => Json("""{"threadId":"thread","turnId":"turn","callId":"call","namespace":null,"tool":"lookup","arguments":{"value":"exact"}}""");
    private static JsonElement DynamicResponse => Json("""{"success":true,"contentItems":[{"type":"inputText","text":"result"},{"type":"inputImage","imageUrl":"data:image/png;base64,abc"},{"type":"inputAudio","audioUrl":"data:audio/wav;base64,abc"}],"future":42}""");
    private static JsonElement InputParameters => Json("""
        {"threadId":"thread","turnId":"turn","itemId":"item","isBlocking":false,"autoResolutionMs":1000,
         "questions":[{"id":"question","header":"Answer","question":"Which option?","isOther":true,"isSecret":true,
         "options":[{"label":"One","description":"First"}]}]}
        """);
    private static ValueTask<bool> Allow(CodexToolRequestAuthorization request, CancellationToken ct) => ValueTask.FromResult(true);

    [Fact]
    public async Task ExactAdmissionAndTerminalResponseSurviveNewLedgerAndCannotBeReapplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var storage = new Storage();
        var ledger = new CodexToolRequestLedger(storage, Allow, clock: clock);
        var expires = clock.Now.AddMinutes(5);
        var admitted = await ledger.AdmitAsync(Dynamic, DynamicParameters, expires, ct);
        Assert.True(admitted.Created);
        Assert.True(admitted.Record.IsBlocking);
        Assert.False((await ledger.AdmitAsync(Dynamic, DynamicParameters, expires, ct)).Created);
        Assert.Equal(CodexToolRequestDisposition.Applied,
            await ledger.RespondAsync(Dynamic, "authenticated-host", DynamicResponse, ct));
        var recovered = new CodexToolRequestLedger(storage, Allow, clock: clock);
        var terminal = await recovered.WaitAsync(Dynamic, ct);
        Assert.Equal(CodexToolRequestState.Completed, terminal.State);
        Assert.Equal(2, terminal.Revision);
        Assert.Equal(42, terminal.Response!.Value.GetProperty("future").GetInt32());
        Assert.Equal(CodexToolRequestDisposition.AlreadyTerminal,
            await recovered.RespondAsync(Dynamic, "authenticated-host", DynamicResponse, ct));
        Assert.False((await recovered.AdmitAsync(Dynamic, DynamicParameters, expires, ct)).Created);
        Assert.DoesNotContain("result", terminal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameConnectionIdCannotAliasDifferentThreadTurnItemOrKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var ledger = new CodexToolRequestLedger(new Storage(), Allow, clock: clock);
        var expires = clock.Now.AddMinutes(1);
        await ledger.AdmitAsync(Dynamic, DynamicParameters, expires, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.AdmitAsync(Input, InputParameters, expires, ct));
        var wrong = Dynamic with { ItemId = "different" };
        Assert.Equal(CodexToolRequestDisposition.CorrelationMismatch,
            await ledger.RespondAsync(wrong, "host", DynamicResponse, ct));
        Assert.Equal(CodexToolRequestDisposition.Missing, await ledger.RespondAsync(
            Dynamic with { Key = Key with { AccountId = "other" } }, "host", DynamicResponse, ct));
        Assert.Equal(CodexToolRequestDisposition.Missing, await ledger.RespondAsync(
            Dynamic with { Key = Key with { ConnectionId = "other" } }, "host", DynamicResponse, ct));
        var stringId = Dynamic with { Key = Key with { RequestId = CodexToolRequestId.Parse(Json("\"1\"")) } };
        Assert.NotEqual(Key.RequestId, stringId.Key.RequestId);
        Assert.True((await ledger.AdmitAsync(stringId, DynamicParameters, expires, ct)).Created);
        Assert.Equal(CodexToolRequestState.Pending, (await ledger.ReadAsync(Dynamic, ct))!.State);
    }

    [Fact]
    public async Task CancelAndExpiryExcludeLateDynamicResponses()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var ledger = new CodexToolRequestLedger(new Storage(), Allow, clock: clock);
        var expires = clock.Now.AddSeconds(10);
        await ledger.AdmitAsync(Dynamic, DynamicParameters, expires, ct);
        Assert.Equal(CodexToolRequestDisposition.Applied, await ledger.CancelAsync(Dynamic, "host", ct));
        Assert.Equal(CodexToolRequestDisposition.AlreadyTerminal, await ledger.RespondAsync(Dynamic, "host", DynamicResponse, ct));
        Assert.Null((await ledger.ReadAsync(Dynamic, ct))!.Response);
        var second = Dynamic with { Key = Key with { RequestId = new("2", true) } };
        await ledger.AdmitAsync(second, DynamicParameters, expires, ct);
        clock.Now = expires;
        Assert.Equal(CodexToolRequestDisposition.AlreadyTerminal, await ledger.RespondAsync(second, "host", DynamicResponse, ct));
        Assert.Equal(CodexToolRequestState.Expired, (await ledger.ReadAsync(second, ct))!.State);
    }

    [Fact]
    public async Task NonblockingUserInputDoesNotRequireWaitingAndRejectsUnknownQuestionIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var storage = new Storage();
        var ledger = new CodexToolRequestLedger(storage, Allow, clock: clock);
        var admitted = await ledger.AdmitAsync(Input, InputParameters, clock.Now.AddMinutes(1), ct);
        Assert.False(admitted.Record.IsBlocking);
        Assert.Equal(CodexToolRequestState.Pending, admitted.Record.State);
        await Assert.ThrowsAsync<CodexProtocolException>(() => ledger.RespondAsync(Input, "host",
            Json("""{"answers":{"unknown":{"answers":["One"]}}}"""), ct));
        Assert.Equal(CodexToolRequestState.Pending, (await ledger.ReadAsync(Input, ct))!.State);
        using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiter = ledger.WaitAsync(Input, waiterCancellation.Token);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(CodexToolRequestState.Pending, (await ledger.ReadAsync(Input, ct))!.State);
        var other = new CodexToolRequestLedger(storage, Allow, clock: clock);
        Assert.Equal(CodexToolRequestDisposition.Applied, await other.RespondAsync(Input, "host",
            Json("""{"answers":{"question":{"answers":["free text"]}}}"""), ct));
        var reply = await ledger.WaitAsync(Input, ct);
        Assert.Equal("free text", reply.Response!.Value.GetProperty("answers").GetProperty("question").GetProperty("answers")[0].GetString());
        Assert.DoesNotContain("free text", reply.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyUserInputDefaultsToBlockingAndDoesNotAutoResolve()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var ledger = new CodexToolRequestLedger(new Storage(), Allow, clock: clock);
        var parameters = Json("""{"threadId":"thread","turnId":"turn","itemId":"item","autoResolutionMs":1,"questions":[{"id":"q","header":"Q","question":"Answer?"}]}""");
        var admitted = await ledger.AdmitAsync(Input, parameters, clock.Now.AddMinutes(1), ct);
        Assert.True(admitted.Record.IsBlocking);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(CodexToolRequestState.Pending, (await ledger.ReadAsync(Input, ct))!.State);
    }

    [Fact]
    public async Task HostDenialAndNativeVerificationProofAreIndependentOfElicitationAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Storage();
        var clock = new Clock();
        var ledger = new CodexToolRequestLedger(storage, (request, _) => ValueTask.FromResult(request.ActorId == "trusted"), clock: clock);
        var form = Json("""{"threadId":"thread","turnId":null,"serverName":"server","environmentId":"env","mode":"openai/userVerification","title":"Verify","description":"Native proof","challenge":"challenge"}""");
        await ledger.AdmitAsync(Mcp, form, clock.Now.AddMinutes(1), ct);
        var accept = Json("""{"action":"accept","content":{"proof":"test"}}""");
        Assert.Equal(CodexToolRequestDisposition.Denied, await ledger.RespondAsync(Mcp, "untrusted", accept, ct));
        Assert.Equal(CodexToolRequestDisposition.Denied, await ledger.RespondAsync(Mcp, "trusted", accept, ct));
        Assert.Equal(CodexToolRequestState.Pending, (await ledger.ReadAsync(Mcp, ct))!.State);
        var native = new CodexToolRequestLedger(storage, Allow, (request, _) =>
            ValueTask.FromResult(request.ActorId == "trusted" &&
                request.Response!.Value.GetProperty("content").GetProperty("proof").GetString() == "test"), clock);
        Assert.Equal(CodexToolRequestDisposition.Applied, await native.RespondAsync(Mcp, "trusted", accept, ct));
        Assert.Equal(CodexToolRequestState.Completed, (await ledger.ReadAsync(Mcp, ct))!.State);
    }

    [Fact]
    public async Task FormElicitationRetainsExtensionsAndDeclineCannotSmuggleContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var ledger = new CodexToolRequestLedger(new Storage(), Allow, clock: clock);
        var form = Json("""{"threadId":"thread","turnId":null,"serverName":"server","environmentId":"env","mode":"form","message":"Enter data","requestedSchema":{"type":"object","properties":{}},"_meta":{"extension":true}}""");
        await ledger.AdmitAsync(Mcp, form, clock.Now.AddMinutes(1), ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => ledger.RespondAsync(Mcp, "host",
            Json("""{"action":"decline","content":{"unexpected":true}}"""), ct));
        Assert.Equal(CodexToolRequestDisposition.CorrelationMismatch, await ledger.RespondAsync(
            Mcp with { EnvironmentId = "other" }, "host", Json("""{"action":"decline","content":null}"""), ct));
        Assert.Equal(CodexToolRequestDisposition.Applied, await ledger.RespondAsync(Mcp, "host",
            Json("""{"action":"accept","content":{"value":"data"},"_meta":{"extension":1}}"""), ct));
        var terminal = (await ledger.ReadAsync(Mcp, ct))!;
        Assert.Equal(1, terminal.Response!.Value.GetProperty("_meta").GetProperty("extension").GetInt32());
    }

    [Fact]
    public async Task ConcurrentResponsesCommitAtMostOneAndDeadlineIsRecheckedAfterAuthorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var storage = new Storage();
        var count = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new CodexToolRequestLedger(storage, async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 2) release.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, clock: clock);
        await ledger.AdmitAsync(Dynamic, DynamicParameters, clock.Now.AddMinutes(1), ct);
        var first = ledger.RespondAsync(Dynamic, "first", DynamicResponse, ct);
        var second = ledger.RespondAsync(Dynamic, "second", DynamicResponse, ct);
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes, value => value == CodexToolRequestDisposition.Applied);
        Assert.Single(outcomes, value => value == CodexToolRequestDisposition.Conflict);
        var next = Dynamic with { Key = Key with { RequestId = new("next") } };
        var expires = clock.Now.AddSeconds(1);
        var expiring = new CodexToolRequestLedger(storage, (_, _) =>
        {
            clock.Now = expires;
            return ValueTask.FromResult(true);
        }, clock: clock);
        await expiring.AdmitAsync(next, DynamicParameters, expires, ct);
        Assert.Equal(CodexToolRequestDisposition.AlreadyTerminal,
            await expiring.RespondAsync(next, "host", DynamicResponse, ct));
        Assert.Equal(CodexToolRequestState.Expired, (await expiring.ReadAsync(next, ct))!.State);
    }

    [Fact]
    public async Task InvalidWireResponsesAndDuplicateMembersFailWithoutMutatingRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new Clock();
        var ledger = new CodexToolRequestLedger(new Storage(), Allow, clock: clock);
        await ledger.AdmitAsync(Dynamic, DynamicParameters, clock.Now.AddMinutes(1), ct);
        await Assert.ThrowsAsync<CodexProtocolException>(() => ledger.RespondAsync(Dynamic, "host",
            Json("""{"success":true,"success":false,"contentItems":[]}"""), ct));
        await Assert.ThrowsAsync<CodexProtocolException>(() => ledger.RespondAsync(Dynamic, "host",
            Json("""{"success":true,"contentItems":[{"type":"shell","text":"unexpected"}]}"""), ct));
        Assert.Equal(CodexToolRequestState.Pending, (await ledger.ReadAsync(Dynamic, ct))!.State);
    }
}
