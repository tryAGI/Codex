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

    private static CodexResponseOptions BodySettings(Guid thread, Guid turn) => Settings(thread, turn) with
    {
        AutomaticCompaction = new()
        {
            TokenThreshold = 200, BackgroundTokenThreshold = 100, ContextWindowTokens = 3000,
            TokenLimitScope = CodexAutoCompactTokenLimitScope.BodyAfterPrefix,
        },
    };

    [Fact]
    public void BodyScopeCountsOnlyGrowthAndFullWindowRemainsIndependent()
    {
        var settings = BodySettings(Guid.NewGuid(), Guid.NewGuid());
        var sample = CodexContextWindow.Capture(settings, null, Owner, [User],
            Terminal(settings, ""","usage":{"input_tokens":1200,"output_tokens":40,"total_tokens":999999},"context_window":{"used_tokens":1240,"total_tokens":3000}"""))!;
        var status = CodexContextWindow.Evaluate(settings, [User, Answer], sample, null, Owner);
        Assert.Equal(1200, status.Prefill!.InputTokens);
        Assert.False(status.Prefill.IsEstimated);
        Assert.Equal(40, status.AutoCompactScopeTokens);
        Assert.Equal(160, status.BaseWindowTokensRemaining);
        Assert.False(status.TokenLimitReached);
        Assert.False(status.BackgroundThresholdReached);
        var next = CodexContextWindow.Capture(settings, null, Owner, [User, Answer, User],
            Terminal(settings, ""","usage":{"input_tokens":1280,"output_tokens":140,"total_tokens":999999}"""), sample)!;
        status = CodexContextWindow.Evaluate(settings, [User, Answer, User, Answer], next, null, Owner);
        Assert.Equal(sample.Prefill, next.Prefill);
        Assert.Null(status.Observation!.UsedTokens);
        Assert.Equal(1420, status.ResponseUsageContextEstimate);
        Assert.Equal(220, status.AutoCompactScopeTokens);
        Assert.True(status.TokenLimitReached);
        Assert.False(status.FullContextWindowLimitReached);
        var smallWindow = settings with
        {
            AutomaticCompaction = settings.AutomaticCompaction! with { ContextWindowTokens = 1300, TokenThreshold = 5000 },
        };
        status = CodexContextWindow.Evaluate(smallWindow, [User, Answer, User, Answer], next, null, Owner);
        Assert.True(status.FullContextWindowLimitReached);
        Assert.True(status.TokenLimitReached);
        Assert.Equal(0, status.BaseWindowTokensRemaining);
    }

    [Fact]
    public void FirstObservedPrefillReplacesEstimateThenNeverMovesWithinWindow()
    {
        var settings = BodySettings(Guid.NewGuid(), Guid.NewGuid()) with { RequiredFirstFunction = "read" };
        var initial = CodexContextWindow.Capture(settings, null, Owner, [User], Terminal(settings, ""))!;
        Assert.True(initial.Prefill!.IsEstimated);
        var status = CodexContextWindow.Evaluate(settings, [User, Answer], initial, null, Owner);
        Assert.True(status.PrefillAnchoredEstimate);
        Assert.Equal(status.EstimatedAdditionalTokens, status.AutoCompactScopeTokens);
        var laterSettings = settings with { RequiredFirstFunction = null };
        var measured = CodexContextWindow.Capture(laterSettings, null, Owner, [User, Answer, User],
            Terminal(laterSettings, ""","usage":{"input_tokens":400,"output_tokens":30}"""), initial)!;
        Assert.False(measured.Prefill!.IsEstimated);
        Assert.Equal(400, measured.Prefill.InputTokens);
        var omitted = CodexContextWindow.Capture(laterSettings, null, Owner, [User, Answer, User, Answer],
            Terminal(laterSettings, ""","usage":{"total_tokens":999999}"""), measured)!;
        Assert.Equal(measured.Prefill, omitted.Prefill);
        status = CodexContextWindow.Evaluate(laterSettings, [User, Answer, User, Answer, Answer], omitted, null, Owner);
        Assert.True(status.PrefillAnchoredEstimate);
        Assert.Null(status.ResponseUsageContextEstimate);
        Assert.Null(status.Observation!.UsedTokens);
        Assert.True(status.ProjectedActiveTokens < 1000);
    }

    [Fact]
    public void BodyBaselineIsFencedByWindowOwnerCheckpointAndExactPrefix()
    {
        var settings = BodySettings(Guid.NewGuid(), Guid.NewGuid());
        var sample = CodexContextWindow.Capture(settings, null, Owner, [User],
            Terminal(settings, ""","usage":{"input_tokens":1200,"output_tokens":40}"""))!;
        var nextWindow = settings with { Identity = settings.Identity! with { ContextWindowId = "window-2" } };
        var status = CodexContextWindow.Evaluate(nextWindow, [User, Answer], sample, null, Owner);
        Assert.Null(status.Prefill);
        Assert.Equal(0, status.AutoCompactScopeTokens);
        Assert.Null(CodexContextWindow.Evaluate(settings, [Answer, User], sample, null, Owner).Prefill);
        Assert.Null(CodexContextWindow.Evaluate(settings, [User, Answer], sample, null, Owner with { UserId = "other" }).Prefill);
        Assert.False(CodexThreadCompaction.ShouldCompact(settings, [User], [Answer], settings.AutomaticCompaction!));
        var next = CodexContextWindow.Capture(nextWindow, null, Owner, [User],
            Terminal(nextWindow, ""","usage":{"input_tokens":0,"output_tokens":20}"""), sample)!;
        Assert.False(next.Prefill!.IsEstimated);
        Assert.Equal(0, next.Prefill.InputTokens);
        Assert.Equal(20, CodexContextWindow.Evaluate(nextWindow, [User, Answer], next, null, Owner).AutoCompactScopeTokens);
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

    private sealed class Server(bool toolFirst = false, bool billingOnly = false, bool scopedUsage = false)
    {
        internal List<JsonElement> Requests { get; } = [];
        private int inferenceCount;
        private bool ToolFirst => toolFirst;
        private bool BillingOnly => billingOnly;
        private bool ScopedUsage => scopedUsage;
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
                    var context = number == 1 && !owner.BillingOnly && !owner.ScopedUsage
                        ? ""","context_window":{"used_tokens":1500,"total_tokens":2000,"remaining_tokens":500}"""
                        : "";
                    var usage = owner.ScopedUsage ? number switch
                    {
                        1 => """{"input_tokens":1200,"output_tokens":40,"total_tokens":1240}""",
                        2 => """{"input_tokens":1280,"output_tokens":140,"total_tokens":1420}""",
                        _ => """{"input_tokens":300,"output_tokens":20,"total_tokens":320}""",
                    } : """{"total_tokens":999999}""";
                    result = "{\"type\":\"response.completed\",\"response\":{\"id\":\"r" + number + "\",\"model\":\"model\",\"output\":" +
                        items + context + ",\"usage\":" + usage + "}}";
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
    public async Task BodyBaselineSurvivesRestartAndNewCompactionWindowStartsWithFreshInput()
    {
        var storage = new RealtimeHistoryTests.Storage();
        var store = new CodexThreadStore(storage, "account");
        var thread = await store.CreateAsync(Guid.NewGuid(), Json("{}"), Ct);
        var budget = new Budget();
        var server = new Server(scopedUsage: true);
        async Task<CodexThreadAgentResult> Run(CodexThreadDocument prior)
        {
            // A fresh runner and store read on every turn prove the baseline is durable.
            var journal = new CodexThreadStore(storage, "account");
            var turn = Guid.NewGuid();
            var settings = BodySettings(prior.Id, turn);
            settings = settings with { AutomaticCompaction = settings.AutomaticCompaction! with { BackgroundTokenThreshold = null } };
            var agent = new CodexThreadAgent(journal, new Credentials(), budget,
                clientFactory: (credentials, usage) => new CodexClient(credentials, handler: server.Handler(), usageObserver: usage));
            var reloaded = await journal.ReadAsync(prior.Id, Ct);
            return await agent.RunAsync(prior.Id, reloaded.Revision, turn, "owner", settings, [User],
                (_, _) => throw new InvalidOperationException("No tools"), cancellationToken: Ct);
        }
        var first = await Run(thread);
        Assert.Equal(1200, first.ContextWindow!.Prefill!.InputTokens);
        Assert.Equal(40, first.ContextWindow.AutoCompactScopeTokens);
        Assert.Null(first.Thread.CompactionCheckpoint);
        var second = await Run(first.Thread);
        Assert.Equal(first.ContextWindow.Prefill, second.ContextWindow!.Prefill);
        Assert.Equal(220, second.ContextWindow.AutoCompactScopeTokens);
        Assert.Null(second.Thread.CompactionCheckpoint);
        var reloadedSecond = await store.ReadAsync(thread.Id, Ct);
        var persisted = CodexContextWindow.ReadObservation(reloadedSecond.Turns[^1], reloadedSecond)!;
        Assert.Equal(first.ContextWindow.Prefill, persisted.Prefill);
        var third = await Run(second.Thread);
        Assert.Equal("compact", third.Thread.CompactionCheckpoint!.ResponseId);
        Assert.Equal(300, third.ContextWindow!.Prefill!.InputTokens);
        Assert.Equal(20, third.ContextWindow.AutoCompactScopeTokens);
        Assert.Equal("r3", third.ContextWindow.Prefill.ResponseId);
        Assert.Equal(4, budget.Completed);
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
