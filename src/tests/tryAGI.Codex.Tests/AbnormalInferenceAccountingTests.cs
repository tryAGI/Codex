using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class AbnormalInferenceAccountingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static readonly JsonElement User = Json("""{"role":"user","content":"hello"}""");
    private const string Incomplete = """
        {"type":"response.incomplete","response":{"id":"response","status":"incomplete","usage":{"input_tokens":12,"output_tokens_details":{"reasoning_tokens":0}}}}
        """;
    private const string Failed = """
        {"type":"response.failed","response":{"id":"response","status":"failed","usage":{"input_tokens":12,"output_tokens_details":{"reasoning_tokens":0}}}}
        """;
    private const string Rejected = """
        {"type":"response.completed","response":{"id":"response","output":null,"usage":{"input_tokens":12,"output_tokens":3}}}
        """;
    private const string Mismatched = """
        {"type":"response.incomplete","response_id":"other","response":{"id":"response","usage":{"input_tokens":12}}}
        """;

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new CodexCredentials("test", "account"));
    }

    private sealed class Handler(string terminal) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + terminal + "\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class Ledger(bool billed, bool failureAfterCommit) : ICodexUsageObserver,
        ICodexInferenceAccountingReconciler, ICodexAbnormalInferenceAccountingReconciler
    {
        private string? id;
        private string? hash;
        private readonly object gate = new();
        private bool settled;
        internal int Reservations, Completed, Incomplete, Commits, Reconciles;
        internal CodexObservedUsage? Usage;
        internal Func<Task>? InspectDurable;
        internal bool ThrowOnAccounting = true;

        public Task ReserveAsync(string requestId, string model, CancellationToken ct)
        {
            Assert.Equal("model", model);
            id = requestId;
            Reservations++;
            return Task.CompletedTask;
        }
        public Task PreparedAsync(string requestId, string model, JsonElement request, CancellationToken ct)
        {
            Assert.Equal(id, requestId);
            hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.GetRawText())));
            return Task.CompletedTask;
        }
        public async Task CompleteAsync(string requestId, string model, JsonElement response, CancellationToken ct)
        {
            Completed++;
            Assert.True(billed);
            Assert.Equal(id, requestId);
            if (InspectDurable is not null) await InspectDurable();
            Settle();
        }
        public Task IncompleteAsync(string requestId, string model, CancellationToken ct)
            => IncompleteWithUsageAsync(requestId, model, null, ct);
        public async Task IncompleteWithUsageAsync(string requestId, string model, CodexObservedUsage? usage, CancellationToken ct)
        {
            Incomplete++;
            if (usage is null) return; // Unknown accounting fallback cannot overwrite a known observation.
            Assert.False(billed);
            Assert.Equal(id, requestId);
            Usage = usage;
            if (InspectDurable is not null) await InspectDurable();
            Settle();
        }
        private void Settle()
        {
            if (!ThrowOnAccounting || failureAfterCommit)
            {
                settled = true;
                Commits++;
            }
            if (ThrowOnAccounting) throw new IOException("Ledger acknowledgement unavailable.");
        }
        public Task<CodexInferenceAccountingOutcome> ReconcileCompletedAsync(CodexInferenceAccountingReceipt receipt, CancellationToken ct)
            => throw new InvalidOperationException("Unvalidated accounting must never call the completed-inference ledger.");

        public Task<CodexInferenceAccountingOutcome> ReconcileObservedAsync(CodexInferenceAccountingReceipt receipt, CancellationToken ct)
        {
            lock (gate)
            {
                Reconciles++;
                Assert.Equal(id, receipt.RequestId);
                Assert.Equal(hash, receipt.RequestSha256);
                Assert.Equal("response", receipt.ResponseId);
                Assert.Equal(billed ? "completed_unvalidated" : "incomplete_observation", receipt.Binding.AccountingKind);
                Assert.Equal(CodexRetainedJournalMetadata.ItemHash(receipt.Terminal), receipt.Binding.TerminalSha256);
                if (!billed)
                {
                    var reconstructed = CodexObservedUsage.Read(receipt.Terminal, requestId: receipt.RequestId,
                        observedAt: receipt.Binding.ObservedAt)!;
                    Assert.Equal(Usage!.EventType, reconstructed.EventType);
                    Assert.Equal(Usage.ObservedAt, reconstructed.ObservedAt);
                    Assert.Equal(Usage.Tokens, reconstructed.Tokens);
                    Assert.Equal(12, reconstructed.Tokens!.InputTokens);
                    Assert.Equal(0, reconstructed.Tokens.ReasoningTokens);
                    Assert.Null(reconstructed.Tokens.OutputTokens);
                }
                if (settled) return Task.FromResult(CodexInferenceAccountingOutcome.AlreadySettled);
                settled = true;
                Commits++;
                return Task.FromResult(CodexInferenceAccountingOutcome.Settled);
            }
        }
    }

    [Theory]
    [InlineData("rejected", false, false)]
    [InlineData("rejected", true, false)]
    [InlineData("incomplete", false, false)]
    [InlineData("incomplete", true, false)]
    [InlineData("failed", false, false)]
    [InlineData("rejected", false, true)]
    [InlineData("incomplete", true, true)]
    public async Task AccountingOnlyEvidenceIsDurableBeforeLedgerAndNeverRecoversOutput(
        string kind, bool failureAfterCommit, bool detached)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        if (detached)
        {
            var previous = Guid.NewGuid();
            thread = await store.StartTurnAsync(thread.Id, thread.Revision, previous, "owner", [User], cancellationToken: Ct);
            thread = await store.CompleteAsync(thread.Id, thread.Revision, previous, "owner", "completed", [], Ct);
        }
        var turn = Guid.NewGuid();
        var billed = kind == "rejected";
        var terminal = kind switch { "rejected" => Rejected, "failed" => Failed, _ => Incomplete };
        var ledger = new Ledger(billed, failureAfterCommit);
        var handler = new Handler(terminal);
        var agent = new CodexThreadAgent(store, new Credentials(), ledger,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: handler, usageObserver: usage));
        var settings = new CodexResponseOptions("model", "instructions")
        {
            Identity = new("session", thread.Id.ToString(), turn.ToString()),
            AutomaticCompaction = detached ? new() { TokenThreshold = 1 } : null,
        };
        ledger.InspectDurable = async () =>
        {
            var current = await store.ReadAsync(thread.Id, Ct);
            if (detached)
                Assert.NotNull(Assert.Single(current.DetachedCompactionReceipts!).Binding.AccountingKind);
            else
                Assert.NotNull(Assert.Single(current.Turns[^1].Dispatches).AbnormalAccounting);
        };
        await Assert.ThrowsAsync<IOException>(() => agent.RunAsync(thread.Id, thread.Revision, turn, "owner", settings,
            [User], (_, _) => throw new InvalidOperationException("Never execute unvalidated output."), cancellationToken: Ct));
        var restarted = new CodexThreadStore(storage, "account");
        var loaded = await restarted.ReadAsync(thread.Id, Ct);
        Assert.Null(loaded.CompactionCheckpoint);
        string requestId;
        CodexStoredInferenceAccounting binding;
        JsonElement evidence;
        if (detached)
        {
            var receipt = Assert.Single(loaded.DetachedCompactionReceipts!);
            requestId = receipt.RequestId;
            binding = receipt.Binding;
            evidence = receipt.Terminal;
            Assert.Equal(CodexInferenceAccountingOutcome.NoEvidence,
                await restarted.ReconcileDetachedCompactionAccountingAsync(loaded.Id, loaded.Revision, requestId, ledger, Ct));
        }
        else
        {
            var native = Assert.Single(loaded.Turns);
            Assert.Equal("failed", native.Status);
            Assert.Empty(native.Output);
            var receipt = Assert.Single(native.Dispatches);
            Assert.Equal(billed ? "succeeded" : "unknown", receipt.State);
            Assert.Null(receipt.MailSampling);
            Assert.Null(receipt.InferenceCompletion);
            requestId = receipt.CallId;
            binding = receipt.AbnormalAccounting!;
            evidence = receipt.Result!.Value;
            Assert.Equal(CodexInferenceAccountingOutcome.NoEvidence,
                await restarted.ReconcileInferenceAccountingAsync(loaded.Id, loaded.Revision, turn, requestId, ledger, Ct));
            Assert.Empty(CodexInferenceRecovery.RecoverOutput(native));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SettleAsync(loaded.Id,
                loaded.Revision, turn, "owner", requestId, "succeeded", Json("""{"id":"response"}"""), Ct));
        }

        Assert.Equal(turn, binding.OriginTurnId);
        Assert.Equal(detached ? "compaction" : null, binding.RequestKind);
        Assert.True(JsonElement.DeepEquals(billed ? Json(terminal).GetProperty("response") : Json(terminal), evidence));
        var writes = storage.Writes;
        Task<CodexInferenceAccountingOutcome> Reconcile() => restarted.ReconcileAbnormalInferenceAccountingAsync(
            loaded.Id, loaded.Revision, detached ? null : turn, requestId, ledger, Ct);
        Assert.Equal(failureAfterCommit ? CodexInferenceAccountingOutcome.AlreadySettled : CodexInferenceAccountingOutcome.Settled,
            await Reconcile());
        Assert.Equal(CodexInferenceAccountingOutcome.AlreadySettled, await Reconcile());
        Assert.Equal(1, ledger.Commits);
        Assert.Equal(2, ledger.Reconciles);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, ledger.Reservations);
        Assert.Equal(writes, storage.Writes);
        var fork = await restarted.ForkAsync(loaded.Id, loaded.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Equal(detached ? CodexInferenceAccountingOutcome.NoEvidence : CodexInferenceAccountingOutcome.InheritedReceipt,
            await restarted.ReconcileAbnormalInferenceAccountingAsync(fork.Id, fork.Revision,
                detached ? null : turn, requestId, ledger, Ct));
        Assert.Equal(2, ledger.Reconciles);
    }

    [Fact]
    public async Task MismatchedResponseIdentityCannotCreateAccountingEvidence()
    {
        var store = new CodexThreadStore(new RealtimeHistoryTests.Storage(), "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var turn = Guid.NewGuid();
        var ledger = new Ledger(false, false);
        var handler = new Handler(Mismatched);
        var agent = new CodexThreadAgent(store, new Credentials(), ledger,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: handler, usageObserver: usage));
        await Assert.ThrowsAsync<CodexProtocolException>(() => agent.RunAsync(thread.Id, thread.Revision, turn, "owner",
            new CodexResponseOptions("model", "instructions") { Identity = new("session", thread.Id.ToString(), turn.ToString()) },
            [User], (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct));
        var loaded = await store.ReadAsync(thread.Id, Ct);
        var receipt = Assert.Single(loaded.Turns[0].Dispatches);
        Assert.Equal("unknown", receipt.State);
        Assert.Null(receipt.AbnormalAccounting);
        Assert.Null(receipt.Result);
        Assert.Equal(CodexInferenceAccountingOutcome.NoEvidence,
            await store.ReconcileAbnormalInferenceAccountingAsync(loaded.Id, loaded.Revision, turn, receipt.CallId, ledger, Ct));
        Assert.Equal(0, ledger.Reconciles);
        Assert.Equal(1, handler.Requests);
    }
}
