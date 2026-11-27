using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class CodexContextWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly JsonElement User = Json("""{"role":"user","content":"test"}""");
    private static readonly JsonElement Answer = Json("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}""");
    private static readonly CodexThreadCompactionScheduler.AuthOwner Owner = new("account", "user", false);

    private static CodexResponseOptions Settings(Guid thread, Guid turn) =>
        new("model", "instructions")
        {
            Identity = new("session", thread.ToString(), turn.ToString()) { ContextWindowId = "window-1" },
            AutomaticCompaction = new() { TokenThreshold = 1000, BackgroundTokenThreshold = 500 },
        };

    private static CodexResponseEvent Terminal(CodexResponseOptions settings, string extra,
        IReadOnlyList<JsonElement>? output = null)
        => new("response.completed", Json("""{"type":"response.completed","response":{"id":"response","model":"model","output":[]""" + extra + "}}"), 1)
        {
            Identity = settings.Identity, RequestId = "request", ResponseId = "response", CompletedOutput = output ?? [Answer],
        };

    [Fact]
    public void MeasuredContextAndEstimatedSuffixRemainSeparate()
    {
        var settings = Settings(Guid.NewGuid(), Guid.NewGuid());
        var terminal = Terminal(settings, ""","context_window":{"used_tokens":650,"total_tokens":800,"remaining_tokens":150},"usage":{"total_tokens":999999}""");
        var observation = CodexContextWindow.Capture(settings, null, Owner, [User], terminal)!;
        Assert.Equal(650, observation.UsedTokens);
        Assert.Equal(800, observation.ModelContextWindow);
        Assert.Equal(150, observation.RemainingTokens);
        var status = CodexContextWindow.Evaluate(settings, [User, Answer], observation, null, Owner);
        Assert.Equal(650, status.ProjectedActiveTokens);
        Assert.Equal(0, status.EstimatedAdditionalTokens);
        Assert.True(status.BackgroundThresholdReached);
        Assert.False(status.TokenLimitReached);
        var appended = Json(new JsonObject { ["role"] = "user", ["content"] = new string('x', 800) }.ToJsonString());
        status = CodexContextWindow.Evaluate(settings, [User, Answer, appended], observation, null, Owner);
        Assert.Equal(650, status.Observation!.UsedTokens);
        Assert.Equal(650 + status.EstimatedAdditionalTokens, status.ProjectedActiveTokens);
        Assert.True(status.TokenLimitReached);
        Assert.Equal(800, status.EffectiveContextLimit);
    }

    [Fact]
    public void BillingTotalsCannotInventContextAndZeroRemainsObserved()
    {
        var settings = Settings(Guid.NewGuid(), Guid.NewGuid());
        var billing = Terminal(settings, ""","usage":{"total_tokens":999999,"input_tokens":800000,"output_tokens":199999}""");
        Assert.Null(CodexContextWindow.Capture(settings, null, Owner, [User], billing));
        var estimate = CodexContextWindow.Evaluate(settings, [User, Answer], null, null, Owner);
        Assert.Null(estimate.Observation);
        Assert.False(estimate.TokenLimitReached);
        var zero = Terminal(settings, ""","context_window":{"used_tokens":0,"remaining_tokens":1000,"total_tokens":1000}""");
        var observed = CodexContextWindow.Capture(settings, null, Owner, [User], zero)!;
        var status = CodexContextWindow.Evaluate(settings, [User, Answer], observed, null, Owner);
        Assert.Equal(0, status.ProjectedActiveTokens);
        Assert.Equal(0, status.Observation!.UsedTokens);
    }

    [Fact]
    public void ExplicitWindowCanConstrainFallbackAndHostEffectivePercent()
    {
        var settings = Settings(Guid.NewGuid(), Guid.NewGuid()) with
        {
            AutomaticCompaction = new() { TokenThreshold = 10000, ContextWindowTokens = 1000, EffectiveContextWindowPercent = 90 },
        };
        var terminal = Terminal(settings, ""","context_window":{"used_tokens":730,"total_tokens":800}""");
        var observed = CodexContextWindow.Capture(settings, null, Owner, [User], terminal);
        var status = CodexContextWindow.Evaluate(settings, [User, Answer], observed, null, Owner);
        Assert.Equal(720, status.EffectiveContextLimit);
        Assert.True(status.TokenLimitReached);
        var capacityOnly = Terminal(settings, ""","context_window":{"total_tokens":800},"usage":{"input_tokens":999999}""");
        observed = CodexContextWindow.Capture(settings, null, Owner, [User], capacityOnly);
        status = CodexContextWindow.Evaluate(settings, [User, Answer], observed, null, Owner);
        Assert.Null(status.Observation!.UsedTokens);
        Assert.Equal(720, status.EffectiveContextLimit);
        Assert.Equal(CodexThreadCompaction.EstimateTokens(settings, [User, Answer]), status.ProjectedActiveTokens);
    }

    [Fact]
    public void ObservationIsScopedToExactPrefixModelWindowOwnerAndCheckpoint()
    {
        var settings = Settings(Guid.NewGuid(), Guid.NewGuid());
        var observed = CodexContextWindow.Capture(settings, null, Owner, [User],
            Terminal(settings, ""","context_window":{"used_tokens":1500,"total_tokens":2000}"""))!;
        var newTurn = settings with { Identity = settings.Identity! with { TurnId = Guid.NewGuid().ToString() } };
        Assert.True(CodexContextWindow.Evaluate(newTurn, [User, Answer], observed, null, Owner).TokenLimitReached);
        foreach (var changed in new[]
        {
            settings with { Model = "other" },
            settings with { Instructions = "different" },
            settings with { Identity = settings.Identity! with { ContextWindowId = "window-2" } },
            settings with { Identity = settings.Identity! with { ThreadId = Guid.NewGuid().ToString() } },
            settings with { Tools = [Json("""{"type":"function","name":"new"}""")] },
        })
            Assert.Null(CodexContextWindow.Evaluate(changed, [User, Answer], observed, null, Owner).Observation);
        Assert.Null(CodexContextWindow.Evaluate(settings, [Answer, User], observed, null, Owner).Observation);
        Assert.Null(CodexContextWindow.Evaluate(settings, [User, Answer], observed, null, Owner with { UserId = "other" }).Observation);
        var checkpoint = new CodexStoredCompactionCheckpoint
        {
            ThreadId = Guid.NewGuid(), SourceRevision = 1, ThroughTurnCount = 1,
            HistorySha256 = "history", ScopeSha256 = "scope", Model = "model", ResponseId = "new-compaction",
            Projection = [], InstalledAt = DateTimeOffset.UtcNow,
        };
        Assert.Null(CodexContextWindow.Evaluate(settings, [User, Answer], observed, checkpoint, Owner).Observation);
        // Different formatting of the same history remains valid across storage round trips.
        var reformatted = Json("""{ "role": "user", "content": "test" }""");
        Assert.NotNull(CodexContextWindow.Evaluate(settings, [reformatted, Answer], observed, null, Owner).Observation);
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new CodexCredentials("token", "account"));
    }

    private sealed class Budget : ICodexUsageObserver
    {
        internal int Completed;
        public Task ReserveAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct)
        { Interlocked.Increment(ref Completed); return Task.CompletedTask; }
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Server(bool toolFirst = false, bool billingOnly = false)
    {
        internal List<JsonElement> Requests { get; } = [];
        private int inferenceCount;
        private bool ToolFirst => toolFirst;
        private bool BillingOnly => billingOnly;
        internal HttpMessageHandler Handler() => new Transport(this);

        private sealed class Transport(Server owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var payload = Json(await request.Content!.ReadAsStringAsync(ct));
                owner.Requests.Add(payload);
                var compacting = payload.GetProperty("input").EnumerateArray().Any(item =>
                    CodexRealtimeProtocol.Text(item, "type") == "compaction_trigger");
                string result;
                if (compacting)
                    result = """{"type":"response.completed","response":{"id":"compact","context_window":{"used_tokens":999999},"output":[{"type":"compaction","encrypted_content":"checkpoint"}]}}""";
                else
                {
                    var number = ++owner.inferenceCount;
                    var items = owner.ToolFirst && number == 1
                        ? """[{"type":"function_call","call_id":"read-once","name":"read","arguments":"{}"}]"""
                        : "[" + Answer.GetRawText() + "]";
                    var context = number == 1 && !owner.BillingOnly
                        ? ""","context_window":{"used_tokens":1500,"total_tokens":2000,"remaining_tokens":500}"""
                        : "";
                    result = "{\"type\":\"response.completed\",\"response\":{\"id\":\"r" + number + "\",\"model\":\"model\",\"output\":" +
                        items + context + ",\"usage\":{\"total_tokens\":999999}}}";
                }
                return new(HttpStatusCode.OK) { Content = new StringContent("data: " + result + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderContextTriggersInlineCompactionButBillingTotalsAloneDoNot(bool billingOnly)
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var budget = new Budget();
        var server = new Server(toolFirst: true, billingOnly: billingOnly);
        var agent = new CodexThreadAgent(store, new Credentials(), budget,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage));
        var turn = Guid.NewGuid();
        var settings = Settings(thread.Id, turn) with
        {
            Tools = [Json("""{"type":"function","name":"read","parameters":{"type":"object","properties":{}}}""")],
            AutomaticCompaction = new() { TokenThreshold = 1000 },
        };
        var calls = 0;
        var result = await agent.RunAsync(thread.Id, thread.Revision, turn, "owner", settings, [User],
            (_, _) => { calls++; return Task.FromResult(new CodexToolOutput("receipt")); }, cancellationToken: Ct);
        Assert.Equal(1, calls);
        Assert.Equal(billingOnly ? 2 : 3, budget.Completed);
        Assert.Equal(billingOnly ? 2 : 3, server.Requests.Count);
        Assert.Equal(!billingOnly, result.Thread.CompactionCheckpoint is not null);
        Assert.Null(result.ContextWindow!.Observation); // The latest terminal omitted context; never reuse stale compaction usage.
        Assert.Null(result.Thread.Turns[^1].ContextWindowObservation);
        Assert.False(result.ContextWindow.TokenLimitReached);
    }

    [Fact]
    public async Task PersistedObservationTriggersPreTurnCompactionAfterRestart()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var budget = new Budget();
        var server = new Server();
        CodexThreadAgent Agent(CodexThreadStore journal) => new(journal, new Credentials(), budget,
            clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage));
        var first = Guid.NewGuid();
        var settings = Settings(thread.Id, first) with { AutomaticCompaction = new() { TokenThreshold = 1000 } };
        var completed = await Agent(store).RunAsync(thread.Id, thread.Revision, first, "owner", settings, [User],
            (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        Assert.Equal(1500, completed.ContextWindow!.ProjectedActiveTokens);
        Assert.Equal(1500, completed.Thread.Turns[^1].ContextWindowObservation!.UsedTokens);
        var restarted = new CodexThreadStore(storage, "account");
        var observed = await restarted.ReadAsync(thread.Id, Ct);
        var snapshot = CodexContextWindow.ReadObservation(observed.Turns[^1])!;
        Assert.Equal("r1", snapshot.ResponseId);
        Assert.Equal(1500, snapshot.UsedTokens);
        Assert.Contains(observed.Turns[^1].Dispatches, receipt => receipt.CallId == snapshot.RequestId);
        Assert.Null(CodexContextWindow.ReadObservation(observed.Turns[^1] with
        {
            ContextWindowObservation = snapshot with { UsedTokens = 42 },
        }));
        var fork = await restarted.ForkAsync(thread.Id, observed.Revision, Guid.NewGuid(), cancellationToken: Ct);
        Assert.Null(fork.Turns[^1].ContextWindowObservation);
        var next = Guid.NewGuid();
        var result = await Agent(restarted).RunAsync(thread.Id, observed.Revision, next, "owner",
            settings with { Identity = settings.Identity! with { TurnId = next.ToString() } }, [User],
            (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        Assert.Equal(3, budget.Completed);
        Assert.Equal("compact", result.Thread.CompactionCheckpoint!.ResponseId);
        Assert.Null(result.ContextWindow!.Observation);
        Assert.Equal(2, result.Thread.Turns.Length);
    }
}
