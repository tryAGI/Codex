using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class InferenceAccountingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly JsonElement User = Json("""{"role":"user","content":"hello"}""");
    private const string Model = "requested-alias";
    private const string Terminal = """
        {"type":"response.completed","response":{"id":"provider-response","model":"resolved-model","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}],"usage":{"input_tokens":31,"input_tokens_details":{"cached_tokens":20},"output_tokens":9,"output_tokens_details":{"reasoning_tokens":3}}}}
        """;

    private sealed class Credentials : ICodexCredentialProvider
    {
        internal static readonly CodexCredentials Value = new("test", "account");
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Value);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + Terminal + "\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    // Models a host's existing reservation with an atomic, idempotent settlement.
    // The recovery path must not invoke the old CompleteAsync or ReserveAsync again.
    private sealed class Ledger(Guid thread, Guid turn, string failure) :
        ICodexUsageObserver, ICodexInferenceAccountingReconciler
    {
        private readonly object gate = new();
        private string? reservedId;
        private string? reservedModel;
        private string? requestHash;
        private JsonElement? settledTerminal;
        internal int Reserves, Completes, Reconciles, Commits;
        internal CodexInferenceAccountingOutcome? ForcedOutcome;
        internal CodexInferenceAccountingReceipt? Observed;

        public Task ReserveAsync(string requestId, string model, CancellationToken ct)
        {
            Assert.Null(reservedId);
            reservedId = requestId;
            reservedModel = model;
            Reserves++;
            return Task.CompletedTask;
        }

        public Task PreparedAsync(string requestId, string model, JsonElement request, CancellationToken ct)
        {
            Assert.Equal(reservedId, requestId);
            Assert.Equal(reservedModel, model);
            requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.GetRawText())));
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string requestId, string model, JsonElement response, CancellationToken ct)
        {
            Assert.Equal(reservedId, requestId);
            Assert.Equal(reservedModel, model);
            Completes++;
            if (failure == "before") throw new IOException("Ledger write did not commit.");
            settledTerminal = response.Clone();
            Commits++;
            if (failure == "after") throw new IOException("Ledger acknowledgement lost.");
            return Task.CompletedTask;
        }

        public Task IncompleteAsync(string requestId, string model, CancellationToken ct) => Task.CompletedTask;

        public Task<CodexInferenceAccountingOutcome> ReconcileCompletedAsync(CodexInferenceAccountingReceipt receipt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                Reconciles++;
                Observed = receipt;
                if (ForcedOutcome is { } forced) return Task.FromResult(forced);
                if (reservedId != receipt.RequestId)
                    return Task.FromResult(CodexInferenceAccountingOutcome.MissingReservation);
                if (reservedModel != receipt.Binding.RequestedModel || requestHash != receipt.RequestSha256 ||
                    receipt.Binding.OriginThreadId != thread || receipt.Binding.OriginTurnId != turn ||
                    receipt.Binding.AccountId != "account" ||
                    receipt.Binding.AuthOwnerSha256 != CodexStoredInferenceAccounting.FundingOwnerDigest(Credentials.Value) ||
                    receipt.ResponseId != "provider-response" ||
                    receipt.Binding.TerminalSha256 != CodexRetainedJournalMetadata.ItemHash(receipt.Terminal))
                    return Task.FromResult(CodexInferenceAccountingOutcome.Conflict);
                if (settledTerminal is { } prior)
                    return Task.FromResult(JsonElement.DeepEquals(prior, receipt.Terminal)
                        ? CodexInferenceAccountingOutcome.AlreadySettled : CodexInferenceAccountingOutcome.Conflict);
                settledTerminal = receipt.Terminal.Clone();
                Commits++;
                return Task.FromResult(CodexInferenceAccountingOutcome.Settled);
            }
        }
    }

    private static async Task<(RealtimeHistoryTests.Storage Storage, CodexThreadStore Store,
        CodexThreadDocument Thread, Ledger Ledger, Handler Handler)> Run(string failure = "before")
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        var ledger = new Ledger(thread.Id, turn, failure);
        var handler = new Handler();
        var agent = new CodexThreadAgent(store, new Credentials(), ledger,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: handler, usageObserver: usage));
        var settings = new CodexResponseOptions(Model, "instructions")
        {
            Identity = new("session", thread.Id.ToString(), turn.ToString()),
        };
        Task<CodexThreadAgentResult> Execute() => agent.RunAsync(thread.Id, thread.Revision, turn, "owner",
            settings, [User], (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        if (failure is "before" or "after") await Assert.ThrowsAsync<IOException>(Execute);
        else await Execute();
        var restarted = new CodexThreadStore(storage, "account");
        return (storage, restarted, await restarted.ReadAsync(thread.Id, Ct), ledger, handler);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("none")]
    public async Task OriginalReservationReconcilesExactlyOnceWithoutProviderOrJournalWrites(string failure)
    {
        var (storage, store, thread, ledger, handler) = await Run(failure);
        var turn = Assert.Single(thread.Turns);
        var dispatch = Assert.Single(turn.Dispatches);
        var binding = dispatch.InferenceCompletion!.Accounting!;
        Assert.Equal(Model, binding.RequestedModel);
        Assert.Equal("resolved-model", dispatch.Result!.Value.GetProperty("model").GetString());
        Assert.Equal(thread.Id, binding.OriginThreadId);
        Assert.Equal(turn.Id, binding.OriginTurnId);
        var writes = storage.Writes;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.ReconcileInferenceAccountingAsync(thread.Id, thread.Revision, turn.Id, dispatch.CallId, ledger, Ct)));
        Assert.Equal(failure == "before" ? 1 : 0, outcomes.Count(outcome => outcome == CodexInferenceAccountingOutcome.Settled));
        Assert.Equal(failure == "before" ? 7 : 8, outcomes.Count(outcome => outcome == CodexInferenceAccountingOutcome.AlreadySettled));
        Assert.Equal(1, ledger.Reserves);
        Assert.Equal(1, ledger.Completes);
        Assert.Equal(1, ledger.Commits);
        Assert.Equal(8, ledger.Reconciles);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(writes, storage.Writes);
        var observed = ledger.Observed!;
        Assert.True(JsonElement.DeepEquals(dispatch.Result.Value, observed.Terminal));
        Assert.Equal(20, observed.Terminal.GetProperty("usage").GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(3, observed.Terminal.GetProperty("usage").GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());
    }

    [Theory]
    [InlineData(CodexInferenceAccountingOutcome.MissingReservation)]
    [InlineData(CodexInferenceAccountingOutcome.Conflict)]
    [InlineData(CodexInferenceAccountingOutcome.Unknown)]
    public async Task HostCannotBeRetriedAutomaticallyOrAllocateReplacementReservation(CodexInferenceAccountingOutcome outcome)
    {
        var (storage, store, thread, ledger, handler) = await Run();
        ledger.ForcedOutcome = outcome;
        var turn = Assert.Single(thread.Turns);
        var dispatch = Assert.Single(turn.Dispatches);
        var writes = storage.Writes;
        Assert.Equal(outcome, await store.ReconcileInferenceAccountingAsync(
            thread.Id, thread.Revision, turn.Id, dispatch.CallId, ledger, Ct));
        Assert.Equal(1, ledger.Reconciles);
        Assert.Equal(0, ledger.Commits);
        Assert.Equal(1, ledger.Reserves);
        Assert.Equal(1, ledger.Completes);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(writes, storage.Writes);
    }

    [Fact]
    public async Task ForkLegacyUnknownAndStaleRevisionDoNotCallLedger()
    {
        var (_, store, thread, ledger, _) = await Run();
        var turn = Assert.Single(thread.Turns);
        var dispatch = Assert.Single(turn.Dispatches);
        await Assert.ThrowsAsync<CodexThreadConflictException>(() => store.ReconcileInferenceAccountingAsync(
            thread.Id, thread.Revision - 1, turn.Id, dispatch.CallId, ledger, Ct));
        var fork = await store.ForkAsync(thread.Id, thread.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(CodexInferenceAccountingOutcome.InheritedReceipt,
            await store.ReconcileInferenceAccountingAsync(fork.Id, fork.Revision, turn.Id, dispatch.CallId, ledger, Ct));
        Assert.Equal(CodexInferenceAccountingOutcome.NoEvidence,
            await store.ReconcileInferenceAccountingAsync(thread.Id, thread.Revision, turn.Id, "unknown-id", ledger, Ct));
        var legacy = thread with { Turns = [turn with { Dispatches = [dispatch with
        {
            InferenceCompletion = dispatch.InferenceCompletion! with { Accounting = null },
        }] }] };
        var legacyStorage = new RealtimeHistoryTests.Storage();
        Assert.True(await legacyStorage.CompareExchangeAsync(thread.Id, null, CodexThreadStore.Encode(legacy), Ct));
        var legacyStore = new CodexThreadStore(legacyStorage, "account");
        Assert.Equal(CodexInferenceAccountingOutcome.NoEvidence,
            await legacyStore.ReconcileInferenceAccountingAsync(thread.Id, thread.Revision, turn.Id, dispatch.CallId, ledger, Ct));
        Assert.Equal(0, ledger.Reconciles);
    }

    [Fact]
    public async Task ChangedTerminalUsageIsRejectedBeforeLedgerAccess()
    {
        var (_, _, thread, ledger, _) = await Run();
        var turn = Assert.Single(thread.Turns);
        var dispatch = Assert.Single(turn.Dispatches);
        var altered = Json(dispatch.Result!.Value.GetRawText().Replace("\"input_tokens\":31", "\"input_tokens\":99", StringComparison.Ordinal));
        Assert.False(JsonElement.DeepEquals(dispatch.Result.Value, altered));
        var corrupted = thread with { Turns = [turn with { Dispatches = [dispatch with { Result = altered }] }] };
        var storage = new RealtimeHistoryTests.Storage();
        Assert.True(await storage.CompareExchangeAsync(thread.Id, null, CodexThreadStore.Encode(corrupted), Ct));
        var store = new CodexThreadStore(storage, "account");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReconcileInferenceAccountingAsync(
            thread.Id, thread.Revision, turn.Id, dispatch.CallId, ledger, Ct));
        Assert.Equal(0, ledger.Reconciles);
    }
}
