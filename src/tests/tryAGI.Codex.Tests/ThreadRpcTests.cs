using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

public sealed class ThreadRpcTests
{
    private static JsonElement Json(string text) { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
    private static JsonElement Args(JsonObject value) => Json(value.ToJsonString());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly JsonElement Defaults = Args(new JsonObject { ["model"] = "test", ["modelProvider"] = "openai",
        ["cwd"] = Path.GetFullPath(Path.GetTempPath()), ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["approvalsReviewer"] = "user" });
    private static readonly Lazy<JsonElement> PinnedSchema = new(() =>
    {
        using var stream = typeof(ThreadRpcTests).Assembly.GetManifestResourceStream("NativeThreadRpc.PinnedSchema")
            ?? throw new InvalidOperationException("Fixed 0.162.1 contract fixture unavailable; do not silently use latest upstream.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });
    private static void RequiredRoot(JsonElement value, string type)
    {
        var schema = PinnedSchema.Value.GetProperty("definitions").GetProperty("v2").GetProperty(type);
        foreach (var name in schema.GetProperty("required").EnumerateArray())
            Assert.True(value.TryGetProperty(name.GetString() ?? throw new InvalidDataException(), out _), type + " missing " + name.GetString());
    }

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        internal readonly Dictionary<Guid, JsonElement> Rows = [];
        internal Action? AfterToolAdmission;
        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); lock (gate) return Task.FromResult(Rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null); }
        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement next, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? actual = Rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (actual != revision) return Task.FromResult(false);
                Rows[id] = next.Clone();
                if (AfterToolAdmission is { } hook && next.GetProperty("turns").EnumerateArray()
                    .SelectMany(t => t.GetProperty("dispatches").EnumerateArray())
                    .Any(d => d.GetProperty("kind").GetString() == "tool" && d.GetProperty("state").GetString() == "dispatching"))
                { AfterToolAdmission = null; hook(); }
                return Task.FromResult(true);
            }
        }
        internal Task<IReadOnlyList<CodexThreadDocument>> List(CodexThreadStore store)
        { lock (gate) return Task.FromResult<IReadOnlyList<CodexThreadDocument>>(Rows.Values.Select(store.ValidateSnapshot).ToArray()); }
    }
    private sealed class Credentials : ICodexCredentialProvider
    { public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default) => ValueTask.FromResult(new CodexCredentials("test-token", "account")); }
    private sealed class Budget : ICodexUsageObserver
    {
        internal int Reserves;
        public Task ReserveAsync(string id, string model, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Reserves); return Task.CompletedTask; }
        public Task CompleteAsync(string id, string model, JsonElement response, CancellationToken ct) => Task.CompletedTask;
        public Task IncompleteAsync(string id, string model, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Provider : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<JsonElement> Requests = new();
        internal Func<int, string> Before = _ => "";
        internal Func<int, CancellationToken, Task<string>> Reply = (number, _) => Task.FromResult("{\"type\":\"message\",\"id\":\"m_" + number + "\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"answer\"}]}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = Json(await (request.Content ?? throw new InvalidOperationException()).ReadAsStringAsync(ct)); Requests.Enqueue(body);
            var number = Requests.Count;
            var output = await Reply(number, ct);
            var completed = new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject
            { ["id"] = "resp_" + number, ["model"] = body.GetProperty("model").GetString(), ["status"] = "completed",
                ["usage"] = new JsonObject { ["input_tokens"] = 2, ["output_tokens"] = 1 }, ["output"] = new JsonArray(JsonNode.Parse(output)) } };
            return new(HttpStatusCode.OK) { Content = new StringContent(Before(number) + "data: " + completed.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class Fixture
    {
        internal readonly Storage Storage = new();
        internal readonly CodexThreadStore Store;
        internal readonly Provider Provider = new();
        internal readonly Budget Budget = new();
        internal readonly ConcurrentQueue<(string Method, JsonElement Params)> Events = new();
        internal readonly ConcurrentDictionary<string, TaskCompletionSource> Terminals = new();
        internal TaskCompletionSource Completed = Signal();
        internal Func<CodexToolCall, CancellationToken, Task<CodexToolOutput>> Tool = (_, _) => throw new InvalidOperationException("Unexpected tool.");
        internal IReadOnlyList<JsonElement> Tools = [];
        internal CodexAgentControl? LastControl;
        internal Action<CodexResponseOptions>? OptionValidator;
        internal Func<CodexThreadConfigurationContext, CancellationToken, Task<CodexThreadConfigurationSnapshot>>? ConfigurationSource;
        internal Fixture() => Store = new(Storage, "account");
        internal CodexThreadRpc Host(bool brokenNotifications = false) => new(Store, _ => Storage.List(Store), Defaults,
            async (run, started, ct) =>
            {
                LastControl = run.Control;
                var agent = new CodexThreadAgent(Store, new Credentials(), Budget,
                    clientFactory: (credentials, observer) => new(credentials, handler: new Borrowed(Provider), usageObserver: observer));
                return (await agent.RunAsync(run.Thread.Id, run.Thread.Revision, run.TurnId, run.OwnerId,
                    run.Options with { Tools = Tools }, run.Input,
                    (call, token) => call.Name == CodexTurnPlan.ToolName && run.ExecutePlan is { } plan ? plan(call.Input, token) : Tool(call, token), maximumRequests: 4,
                    turnStarted: (d, _) => started(d), cancellationToken: ct, control: run.Control, validatedItem: run.ValidatedItem,
                    observeAsync: run.ObserveProvider)).Thread;
            }, (method, value) =>
            {
                Events.Enqueue((method, value));
                RequiredRoot(value, method switch
                {
                    "turn/started" => "TurnStartedNotification", "turn/completed" => "TurnCompletedNotification",
                    "item/started" => "ItemStartedNotification", "item/completed" => "ItemCompletedNotification",
                    "turn/plan/updated" => "TurnPlanUpdatedNotification", "turn/diff/updated" => "TurnDiffUpdatedNotification",
                    "turn/moderationMetadata" => "TurnModerationMetadataNotification", _ => throw new InvalidDataException()
                });
                if (method == "turn/completed")
                {
                    Completed.TrySetResult();
                    Terminals.GetOrAdd(value.GetProperty("threadId").GetString() ?? throw new InvalidDataException(), _ => Signal()).TrySetResult();
                }
                return brokenNotifications ? Task.FromException(new IOException("subscriber disconnected")) : Task.CompletedTask;
            }, validateOptions: OptionValidator, configurationSource: ConfigurationSource);
    }
    private sealed class Borrowed(Provider provider) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(provider, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => invoker.SendAsync(request, ct);
        protected override void Dispose(bool disposing) { if (disposing) invoker.Dispose(); base.Dispose(disposing); }
    }
    private static async Task<Guid> Create(CodexThreadRpc host)
    {
        var response = await host.InvokeAsync("thread/start", Json("{}"), ct: TestContext.Current.CancellationToken);
        RequiredRoot(response, "ThreadStartResponse"); RequiredRoot(response.GetProperty("thread"), "Thread");
        return Guid.Parse(response.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
    }
    private static JsonElement Input(Guid id, string text = "hello") => Args(new JsonObject
    { ["threadId"] = id.ToString(), ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) });

    [Fact]
    public async Task NativeConfigurationFreshSnapshotReplacesPreviousRequirements()
    {
        var f = new Fixture(); var managed = true;
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot(managed ? "A" : "B",
            [new(CodexConfigurationLayerKind.User, "account", Json(managed ? "{\"model\":\"old\"}" : "{\"model\":\"new\"}"))],
            managed ? Json("{\"model\":[\"old\"]}") : null));
        var host = f.Host(); var id = await Create(host);
        managed = false;
        await host.InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        var thread = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("new", thread.Settings.GetProperty("model").GetString());
        Assert.False(thread.Settings.TryGetProperty("nativeConfigurationRequirements", out _));
        Assert.Equal("B", thread.Settings.GetProperty("nativeConfigurationRevision").GetString());
        Assert.Equal(0, f.Budget.Reserves);
        await VerifyFileConfigurationLayersAsync();
    }

    private static async Task VerifyFileConfigurationLayersAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(AppContext.BaseDirectory, "codex-config-fixture-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(directory, "home"); var repo = Path.Combine(directory, "repo"); var cwd = Path.Combine(repo, "sub");
        Directory.CreateDirectory(home); Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, ".codex")); Directory.CreateDirectory(Path.Combine(cwd, ".codex"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main", ct);
            foreach (var name in new[] { "package", "admin", "system", "cloud" })
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".toml"), "model = \"" + name + "\"\n", ct);
            var userFile = Path.Combine(home, "config.toml");
            var trust = "[projects." + JsonValue.Create(repo).ToJsonString() + "]\ntrust_level = \"trusted\"\n";
            await File.WriteAllTextAsync(Path.Combine(directory, "system.toml"), "model = \"system\"\n[profiles.work]\nmodel_verbosity = \"low\"\n" + trust.Replace("\"trusted\"", "\"untrusted\"", StringComparison.Ordinal), ct);
            var user = "model = \"file-user\"\nprofile = \"work\"\n[profiles.work]\nmodel_reasoning_effort = \"medium\"\n" + trust;
            await File.WriteAllTextAsync(userFile, user, ct);
            await File.WriteAllTextAsync(Path.Combine(home, "fast.config.toml"), "model = \"profile-file\"", ct);
            await File.WriteAllTextAsync(Path.Combine(repo, ".codex", "config.toml"), "model = \"project\"", ct);
            var nested = Path.Combine(cwd, ".codex", "config.toml");
            await File.WriteAllTextAsync(nested, "model_reasoning_effort = \"high\"", ct);
            var required = Path.Combine(directory, "requirements.toml");
            await File.WriteAllTextAsync(required, "allowed_approval_policies = [\"never\", \"on-request\"]", ct);
            var sourceOptions = new CodexFileConfigurationOptions(home)
            {
                PackagedConfigFile = Path.Combine(directory, "package.toml"), AdminConfigFile = Path.Combine(directory, "admin.toml"),
                SystemConfigFile = Path.Combine(directory, "system.toml"), CloudConfigFiles = [Path.Combine(directory, "cloud.toml")], RequirementsFiles = [required]
            };
            var account = new CodexThreadConfigurationSnapshot("account", [new(CodexConfigurationLayerKind.User, "account", Json("{\"model\":\"account\"}"))], Json("{\"approval_policy\":[\"never\"]}"));
            var source = new CodexFileConfigurationSource(sourceOptions);
            var f = new Fixture { ConfigurationSource = (context, token) => source.LoadAsync(context, account, token) };
            var host = f.Host();
            var created = await host.InvokeAsync("thread/start", Args(new JsonObject { ["cwd"] = cwd }), ct);
            var id = Guid.Parse(created.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
            var stored = await f.Store.ReadAsync(id, ct);
            Assert.Equal("project", stored.Settings.GetProperty("model").GetString());
            Assert.Equal("high", stored.Settings.GetProperty("effort").GetString());
            Assert.Equal("low", stored.Settings.GetProperty("verbosity").GetString());
            Assert.Equal("never", Assert.Single(stored.Settings.GetProperty("nativeConfigurationRequirements").GetProperty("approval_policy").EnumerateArray()).GetString());
            foreach (var method in new[] { "thread/start", "thread/resume", "thread/fork" })
            {
                var args = new JsonObject { ["cwd"] = cwd, ["model"] = "typed" };
                if (method != "thread/start") args["threadId"] = id.ToString();
                var response = await host.InvokeAsync(method, Args(args), ct);
                var target = Guid.Parse(response.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
                Assert.Equal("typed", (await f.Store.ReadAsync(target, ct)).Settings.GetProperty("model").GetString());
            }
            var profile = await new CodexFileConfigurationSource(sourceOptions with { Profile = "fast", IgnoreProjectConfig = true })
                .LoadAsync(new("thread/start", null, cwd), account, ct);
            Assert.Equal("profile-file", profile.Layers.Last().Config.GetProperty("model").GetString());
            await File.WriteAllTextAsync(nested, "invalid TOML = [", ct);
            var rows = f.Storage.Rows.Count;
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/start", Args(new JsonObject { ["cwd"] = cwd }), ct));
            Assert.Equal(rows, f.Storage.Rows.Count);
            await File.WriteAllTextAsync(userFile, user.Replace("\"trusted\"", "\"untrusted\"", StringComparison.Ordinal), ct);
            var disabled = await source.LoadAsync(new("thread/start", null, cwd), account, ct);
            Assert.All(disabled.Layers.Where(layer => layer.Kind == CodexConfigurationLayerKind.Project), layer => Assert.False(layer.Trusted));
            Assert.NotEqual(stored.Settings.GetProperty("nativeConfigurationRevision").GetString(), disabled.Revision);
            await File.WriteAllTextAsync(required, "allowed_approval_policies = [\"on-request\"]", ct);
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => source.LoadAsync(new("thread/start", null, cwd), account, ct));
            Assert.Equal(0, f.Budget.Reserves);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeConfigurationActiveUpdateEnforcesTheAdmittedRequirements()
    {
        var f = new Fixture(); var requested = Signal(); var release = Signal();
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("managed",
            [new(CodexConfigurationLayerKind.User, "account", Json("{\"model\":\"allowed\"}"))], Json("{\"model\":[\"allowed\"]}")));
        f.Provider.Reply = async (_, ct) =>
        {
            requested.TrySetResult(); await release.Task.WaitAsync(ct);
            return "{\"type\":\"message\",\"id\":\"safe\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"safe\"}]}";
        };
        var host = f.Host(); var id = await Create(host);
        var started = await host.InvokeAsync("turn/start", Input(id), TestContext.Current.CancellationToken);
        await requested.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var args = new JsonObject { ["threadId"] = id.ToString(), ["turnId"] = started.GetProperty("turn").GetProperty("id").GetString(), ["model"] = "denied" };
        try
        {
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/settings/update", Args(args), TestContext.Current.CancellationToken));
            args["model"] = "allowed";
            var applied = await host.InvokeAsync("turn/settings/update", Args(args), TestContext.Current.CancellationToken);
            Assert.Equal("applied", applied.GetProperty("status").GetString());
        }
        finally { release.TrySetResult(); }
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("allowed", Assert.Single(f.Provider.Requests).GetProperty("model").GetString());
        Assert.Equal("completed", Assert.Single((await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Turns).Status);
    }

    [Fact]
    public async Task NativeConfigurationOwnedWorkerCannotBypassThreadRequirements()
    {
        var f = new Fixture();
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("managed",
            [new(CodexConfigurationLayerKind.User, "account", Json("{\"model\":\"allowed\"}"))], Json("{\"model\":[\"allowed\"]}")));
        var host = f.Host(); var id = await Create(host);
        var thread = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken); var executed = false;
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.RunOwnedAsync(thread, Guid.CreateVersion7(),
            new CodexResponseOptions("denied", "test"), [], (_, _, _) => { executed = true; return Task.FromResult(thread); }, TestContext.Current.CancellationToken));
        Assert.False(executed);
        Assert.Equal(thread.Revision, (await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Revision);
        Assert.Equal(0, f.Budget.Reserves);
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public async Task NativeConfigurationExplicitNullTierClearsLowerLayersAndSurvivesRefresh(string method)
    {
        var f = new Fixture();
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("tier",
            [new(CodexConfigurationLayerKind.User, "account", Json("{\"service_tier\":\"default\"}"))]));
        var host = f.Host(); var source = await Create(host);
        var args = new JsonObject { ["serviceTier"] = null, ["config"] = new JsonObject { ["service_tier"] = "default" } };
        if (method != "thread/start") args["threadId"] = source.ToString();
        var result = await host.InvokeAsync(method, Args(args), TestContext.Current.CancellationToken);
        var id = Guid.Parse(result.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
        await host.InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        var thread = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, thread.Settings.GetProperty("serviceTier").ValueKind);
        Assert.Equal(JsonValueKind.Null, thread.Settings.GetProperty("nativeConfigurationOverrides").GetProperty("serviceTier").ValueKind);
        await host.InvokeAsync("turn/start", Input(id), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(f.Provider.Requests).TryGetProperty("service_tier", out _));
    }

    [Fact]
    public async Task NativeConfigurationLegacyWorkerChoicesAreNotReplacedByAccountDefaults()
    {
        var f = new Fixture(); var id = Guid.CreateVersion7();
        var saved = JsonNode.Parse(Defaults.GetRawText()) as JsonObject ?? throw new InvalidDataException();
        saved["model"] = "legacy-choice"; saved["effort"] = "high";
        saved["nativeWorker"] = true; saved["nativeSessionId"] = id.ToString();
        await f.Store.CreateAsync(id, Args(saved), TestContext.Current.CancellationToken);
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("fresh-account",
            [new(CodexConfigurationLayerKind.User, "account", Json("{\"model\":\"new-default\",\"model_reasoning_effort\":\"low\"}"))]));
        await f.Host().InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        var restored = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("legacy-choice", restored.Settings.GetProperty("model").GetString());
        Assert.Equal("high", restored.Settings.GetProperty("effort").GetString());
        Assert.Equal("legacy-choice", restored.Settings.GetProperty("nativeConfigurationOverrides").GetProperty("model").GetString());
        Assert.True(restored.Settings.GetProperty("nativeWorker").GetBoolean());
        Assert.Equal(id.ToString(), restored.Settings.GetProperty("nativeSessionId").GetString());
        Assert.Equal(0, f.Budget.Reserves);
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public async Task NativeConfigurationLoadingCannotCreateThreadsAfterHostShutdown(string method)
    {
        var f = new Fixture(); var source = await Create(f.Host());
        var requested = Signal(); var release = Signal();
        f.ConfigurationSource = async (_, ct) =>
        {
            requested.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new CodexThreadConfigurationSnapshot("loaded", []);
        };
        var host = f.Host();
        var args = new JsonObject();
        if (method != "thread/start") args["threadId"] = source.ToString();
        var pending = host.InvokeAsync(method, Args(args), TestContext.Current.CancellationToken);
        await requested.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => pending);
        Assert.Single(f.Storage.Rows);
        Assert.Equal(1, (await f.Store.ReadAsync(source, TestContext.Current.CancellationToken)).Revision);
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public async Task NativeConfigurationLayersRefreshDefaultsAndRetainOnlyExplicitChoices(string method)
    {
        var f = new Fixture(); var revision = "A";
        var validated = new List<CodexResponseOptions>();
        f.OptionValidator = validated.Add;
        f.ConfigurationSource = (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(Defaults.GetProperty("cwd").GetString(), context.WorkingDirectory);
            return Task.FromResult(new CodexThreadConfigurationSnapshot(revision,
            [new(CodexConfigurationLayerKind.System, "system", Json("{\"model\":\"system-model\",\"model_reasoning_effort\":\"low\"}")),
             new(CodexConfigurationLayerKind.User, "account", Args(new JsonObject
             { ["model"] = "account-" + revision, ["model_reasoning_effort"] = revision == "C" ? "medium" : "high",
                 ["developer_instructions"] = "instructions-" + revision })),
             new(CodexConfigurationLayerKind.Project, "untrusted-project", Json("{\"model\":\"injected\",\"mcp_servers\":{\"evil\":{}}}"), Trusted: false)]));
        };
        var host = f.Host(); var source = await Create(host); revision = "B";
        var args = new JsonObject { ["model"] = "typed-model", ["config"] = new JsonObject { ["model"] = "config-model", ["model_verbosity"] = "low" } };
        if (method != "thread/start") args["threadId"] = source.ToString();
        var result = await host.InvokeAsync(method, Args(args), TestContext.Current.CancellationToken);
        var id = Guid.Parse(result.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
        revision = "C";
        await host.InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        var thread = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("typed-model", thread.Settings.GetProperty("model").GetString());
        Assert.Equal("medium", thread.Settings.GetProperty("effort").GetString());
        Assert.Equal("C", thread.Settings.GetProperty("nativeConfigurationRevision").GetString());
        Assert.False(thread.Settings.GetProperty("nativeConfigurationSources")[2].GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("mcp_servers", thread.Settings.GetRawText());
        await host.InvokeAsync("turn/start", Input(id), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var request = Assert.Single(f.Provider.Requests);
        Assert.Equal("typed-model", request.GetProperty("model").GetString());
        Assert.Equal("medium", request.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("low", request.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Contains("instructions-C", request.GetProperty("instructions").GetString());
        Assert.All(validated, options => Assert.Contains("instructions-", options.Instructions));
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public async Task NativeConfigurationRequirementsCannotBeBypassedByRpcOrTurnOverrides(string method)
    {
        var f = new Fixture();
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("managed",
            [new(CodexConfigurationLayerKind.User, "account", Json("{\"model\":\"allowed\"}"))],
            Json("{\"model\":[\"allowed\"],\"service_tier\":[\"auto\",\"default\"]}")));
        var host = f.Host(); var id = await Create(host);
        var before = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        var rows = f.Storage.Rows.Count;
        var args = new JsonObject { ["model"] = "denied" };
        if (method != "thread/start") args["threadId"] = id.ToString();
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync(method, Args(args), TestContext.Current.CancellationToken));
        var turn = new JsonObject { ["threadId"] = id.ToString(), ["model"] = "denied",
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "never dispatched" }) };
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/start", Args(turn), TestContext.Current.CancellationToken));
        Assert.Equal(before.Revision, (await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Revision);
        Assert.Equal(rows, f.Storage.Rows.Count);
        Assert.Equal(0, f.Budget.Reserves);
        Assert.Empty(f.Provider.Requests);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("untrusted-user")]
    [InlineData("duplicate")]
    [InlineData("requirements")]
    public async Task NativeConfigurationInvalidSourcesFailBeforeCreatingAThread(string failure)
    {
        var f = new Fixture();
        IReadOnlyList<CodexThreadConfigurationLayer> layers = failure switch
        {
            "order" => [new(CodexConfigurationLayerKind.User, "user", Json("{}")), new(CodexConfigurationLayerKind.System, "system", Json("{}"))],
            "untrusted-user" => [new(CodexConfigurationLayerKind.User, "user", Json("{}"), Trusted: false)],
            "duplicate" => [new(CodexConfigurationLayerKind.User, "user", Json("{\"model\":\"a\",\"model\":\"b\"}"))],
            _ => []
        };
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot("invalid", layers,
            failure == "requirements" ? Json("{\"model\":[]}") : null));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.Host().InvokeAsync("thread/start", Json("{}"), TestContext.Current.CancellationToken));
        Assert.Empty(f.Storage.Rows);
        Assert.Equal(0, f.Budget.Reserves);
    }

    [Fact]
    public async Task NativeConfigurationKeepsExplicitTurnChoicesAcrossLaterAccountRefresh()
    {
        var f = new Fixture(); var revision = "A";
        f.ConfigurationSource = (_, _) => Task.FromResult(new CodexThreadConfigurationSnapshot(revision,
            [new(CodexConfigurationLayerKind.User, "account", Args(new JsonObject { ["model"] = "account-" + revision,
                ["model_reasoning_effort"] = "low", ["service_tier"] = "default" }))]));
        var host = f.Host(); var id = await Create(host);
        var args = ObjectForTurn(id);
        args["model"] = "turn-model"; args["effort"] = "high"; args["serviceTier"] = null;
        await host.InvokeAsync("turn/start", Args(args), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        revision = "B";
        await host.InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        var thread = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("turn-model", thread.Settings.GetProperty("model").GetString());
        Assert.Equal("high", thread.Settings.GetProperty("effort").GetString());
        Assert.Equal(JsonValueKind.Null, thread.Settings.GetProperty("serviceTier").ValueKind);
        Assert.Equal("B", thread.Settings.GetProperty("nativeConfigurationRevision").GetString());
    }

    private static JsonObject ObjectForTurn(Guid id) => new()
    { ["threadId"] = id.ToString(), ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello" }) };

    [Fact]
    public async Task ResumeConfigRejoinsActiveTurnWithoutChangingItsJournalRevision()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var requested = Signal(); var release = Signal();
        f.Provider.Reply = async (_, ct) =>
        {
            requested.TrySetResult();
            await release.Task.WaitAsync(ct);
            return "{\"type\":\"message\",\"id\":\"resumed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"completed safely\"}]}";
        };
        await host.InvokeAsync("turn/start", Input(id), TestContext.Current.CancellationToken);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            var before = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
            await host.InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString(),
                ["config"] = new JsonObject { ["model"] = "different", ["model_verbosity"] = "high" } }), TestContext.Current.CancellationToken);
            var after = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(before.Revision, after.Revision);
            Assert.True(JsonElement.DeepEquals(before.Settings, after.Settings));
        }
        finally { release.TrySetResult(); }
        await f.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var final = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("completed", Assert.Single(final.Turns).Status);
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public async Task ConfigUsesEffectivePrecedenceAndProviderSettingsBeforeAnyWrite(string method)
    {
        var f = new Fixture(); var host = f.Host();
        var source = await Create(host);
        var parameters = new JsonObject
        {
            ["config"] = new JsonObject { ["model"] = "", ["model_verbosity"] = "low",
                ["model_reasoning_effort"] = "medium", ["model_reasoning_summary"] = "detailed",
                ["developer_instructions"] = "Configured instructions" },
            ["model"] = "effective-model"
        };
        if (method != "thread/start") parameters["threadId"] = source.ToString();
        var result = await host.InvokeAsync(method, Args(parameters), TestContext.Current.CancellationToken);
        var id = Guid.Parse(result.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
        var stored = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("effective-model", stored.Settings.GetProperty("model").GetString());
        Assert.Equal("low", stored.Settings.GetProperty("verbosity").GetString());
        await host.InvokeAsync("turn/start", Input(id), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var request = Assert.Single(f.Provider.Requests);
        Assert.Equal("effective-model", request.GetProperty("model").GetString());
        Assert.Equal("low", request.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("medium", request.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("detailed", request.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Contains("Configured instructions", request.GetProperty("instructions").GetString());
        var before = await f.Store.ReadAsync(source, TestContext.Current.CancellationToken);
        var count = f.Storage.Rows.Count;
        foreach (var config in new[] { "{\"model_verbosity\":\"invalid\"}", "{\"model_verbosity\":false}",
            "{\"features\":{\"multi_agent\":true}}" })
        {
            var invalid = new JsonObject { ["config"] = JsonNode.Parse(config) };
            if (method != "thread/start") invalid["threadId"] = source.ToString();
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync(method, Args(invalid), TestContext.Current.CancellationToken));
            Assert.Equal(count, f.Storage.Rows.Count);
            Assert.Equal(before.Revision, (await f.Store.ReadAsync(source, TestContext.Current.CancellationToken)).Revision);
        }
    }

    [Fact]
    public async Task EphemeralLifecycleNeverWritesStorageAndForkStorageIsExplicit()
    {
        var f = new Fixture(); var host = f.Host();
        var created = await host.InvokeAsync("thread/start", Json("{\"ephemeral\":true,\"serviceName\":\"voice\",\"sessionStartSource\":\"clear\",\"threadSource\":\"watch\",\"config\":{\"model_reasoning_effort\":\"medium\"}}"), TestContext.Current.CancellationToken);
        var id = Guid.Parse(created.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
        Assert.True(created.GetProperty("thread").GetProperty("ephemeral").GetBoolean());
        Assert.Empty(f.Storage.Rows);
        var stored = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("watch", stored.Settings.GetProperty("threadSource").GetString());
        Assert.Equal("clear", stored.Settings.GetProperty("sessionStartSource").GetString());
        Assert.Equal("voice", stored.Settings.GetProperty("serviceName").GetString());
        Assert.Equal("medium", stored.Settings.GetProperty("effort").GetString());
        var loaded = await host.InvokeAsync("thread/loaded/list", Json("{}"), TestContext.Current.CancellationToken);
        Assert.Single(loaded.GetProperty("data").EnumerateArray());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new CodexThreadStore(f.Storage, "account").ReadAsync(id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/read", Args(new JsonObject
            { ["threadId"] = id.ToString(), ["includeTurns"] = true }), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/turns/list", Args(new JsonObject
            { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken));
        var sourceId = await Create(host);
        var forked = await host.InvokeAsync("thread/fork", Args(new JsonObject { ["threadId"] = sourceId.ToString(), ["ephemeral"] = true, ["excludeTurns"] = true,
            ["threadSource"] = "fork-test", ["config"] = new JsonObject { ["model"] = "forked" } }), TestContext.Current.CancellationToken);
        Assert.True(forked.GetProperty("thread").GetProperty("ephemeral").GetBoolean());
        Assert.Equal("fork-test", forked.GetProperty("thread").GetProperty("threadSource").GetString());
        Assert.Single(f.Storage.Rows);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/fork", Args(new JsonObject
            { ["threadId"] = id.ToString(), ["ephemeral"] = null }), TestContext.Current.CancellationToken));
        var revision = stored.Revision;
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/resume", Args(new JsonObject
            { ["threadId"] = id.ToString(), ["excludeTurns"] = true, ["config"] = new JsonObject { ["unknown_config"] = true } }), TestContext.Current.CancellationToken));
        Assert.Equal(revision, (await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task UserSuppliedToolOutputIsInputNotAnExecutedToolReceipt()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var parameters = new JsonObject { ["threadId"] = id.ToString(), ["input"] = new JsonArray(),
            ["toolOutput"] = new JsonObject { ["name"] = "provided_fact", ["namespace"] = "external", ["output"] = "untrusted result" },
            ["turnTrigger"] = "watch", ["disabledPluginIds"] = new JsonArray("example-plugin") };
        await host.InvokeAsync("turn/start", Args(parameters), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var stored = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        var turn = Assert.Single(stored.Turns);
        Assert.Equal("watch", turn.TurnTrigger);
        Assert.DoesNotContain(turn.Dispatches, d => d.Kind == "tool");
        Assert.Equal("provided_fact", f.Provider.Requests.Single().GetProperty("input")[0].GetProperty("name").GetString());
        Assert.Equal("example-plugin", stored.Settings.GetProperty("disabledPluginIds")[0].GetString());
        var history = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        Assert.Contains(history.GetProperty("data").EnumerateArray(), x => x.GetProperty("item").GetProperty("type").GetString() == "functionCallOutput");
        parameters["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "conflict" });
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/start", Args(parameters), TestContext.Current.CancellationToken));
        Assert.Single(f.Provider.Requests);
    }

    [Fact]
    public async Task OwnedWorkerSharesCatalogControlsAndProjectsPixelsOnlyInJournal()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var thread = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        var arrived = Signal(); var release = Signal(); var reply = f.Provider.Reply;
        f.Provider.Reply = async (n, ct) => { if (n == 1) { arrived.TrySetResult(); await release.Task.WaitAsync(ct); } return await reply(n, ct); };
        var turnId = Guid.CreateVersion7();
        var input = Json("{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,PIXELSECRET\"}]}");
        var result = host.RunOwnedAsync(thread, turnId, new("test", "worker") { Identity = new(id.ToString(), id.ToString(), turnId.ToString()) }, [input],
            async (run, started, ct) =>
            {
                var agent = new CodexThreadAgent(f.Store, new Credentials(), f.Budget,
                    clientFactory: (credentials, observer) => new(credentials, handler: new Borrowed(f.Provider), usageObserver: observer));
                return (await agent.RunAsync(run.Thread.Id, run.Thread.Revision, run.TurnId, run.OwnerId, run.Options, run.Input,
                    (_, _) => throw new InvalidOperationException("No worker tool expected"), turnStarted: (document, _) => started(document),
                    cancellationToken: ct, control: run.Control, validatedItem: run.ValidatedItem,
                    journalProjection: CodexThreadImageProjection.Redact)).Thread;
            }, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await host.InvokeAsync("turn/steer", Args(new JsonObject { ["threadId"] = id.ToString(), ["expectedTurnId"] = turnId.ToString(),
            ["clientUserMessageId"] = "client-steer", ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "second request" }) }), TestContext.Current.CancellationToken);
        release.TrySetResult(); var completed = await result.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, f.Provider.Requests.Count);
        Assert.Contains("PIXELSECRET", f.Provider.Requests.First().GetRawText());
        Assert.DoesNotContain("PIXELSECRET", CodexThreadStore.Encode(completed).GetRawText());
        Assert.Equal("client-steer", Assert.Single(completed.Turns).ClientUserMessageIds?[1]);
        Assert.Contains(f.Events, e => e.Method == "turn/started");
        Assert.Contains(f.Events, e => e.Method == "turn/completed");
        var items = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString() }), TestContext.Current.CancellationToken);
        Assert.Contains(items.GetProperty("data").EnumerateArray(), x => x.GetProperty("item").TryGetProperty("clientId", out var client) && client.GetString() == "client-steer");
    }

    [Fact]
    public async Task TurnHostSettingsPersistAndRejectEscalationBeforeWrites()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var before = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        var input = (JsonObject)(JsonNode.Parse(Input(id).GetRawText()) ?? throw new InvalidDataException());
        input["cwd"] = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "native-turn-settings"));
        input["approvalPolicy"] = "never"; input["approvalsReviewer"] = "user";
        input["sandboxPolicy"] = new JsonObject { ["type"] = "readOnly", ["networkAccess"] = true };
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/start", Args(input), TestContext.Current.CancellationToken));
        Assert.Equal(before.Revision, (await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Revision);
        Assert.Empty(f.Provider.Requests);
        input["sandboxPolicy"] = new JsonObject { ["type"] = "readOnly", ["networkAccess"] = false };
        await host.InvokeAsync("turn/start", Args(input), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var saved = await f.Store.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(input["cwd"]?.GetValue<string>(), saved.Settings.GetProperty("cwd").GetString());
        Assert.Equal("read-only", saved.Settings.GetProperty("sandbox").GetString());
        f.Completed = Signal();
        input["cwd"] = null; input["approvalPolicy"] = null; input["approvalsReviewer"] = null; input["sandboxPolicy"] = null;
        await host.InvokeAsync("turn/start", Args(input), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(saved.Settings.GetProperty("cwd").GetString(),
            (await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Settings.GetProperty("cwd").GetString());
        Assert.Equal(2, f.Provider.Requests.Count);
    }

    [Fact]
    public async Task PersistentAndPerTurnTiersAreDistinctAndForkOverridesAreAtomic()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var input = (JsonObject)(JsonNode.Parse(Input(id).GetRawText()) ?? throw new InvalidDataException());
        input["serviceTier"] = "default"; input["serviceTierForTurn"] = "priority"; input["effort"] = "minimal";
        input["personality"] = "friendly";
        await host.InvokeAsync("turn/start", Args(input), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("priority", f.Provider.Requests.Last().GetProperty("service_tier").GetString());
        Assert.Equal("minimal", f.Provider.Requests.Last().GetProperty("reasoning").GetProperty("effort").GetString());
        f.Completed = Signal(); input.Remove("serviceTier"); input["serviceTierForTurn"] = null;
        await host.InvokeAsync("turn/start", Args(input), TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("default", f.Provider.Requests.Last().GetProperty("service_tier").GetString());
        var fork = await host.InvokeAsync("thread/fork", Args(new JsonObject { ["threadId"] = id.ToString(),
            ["model"] = "fork-model", ["serviceTier"] = "priority" }), TestContext.Current.CancellationToken);
        var forkId = Guid.Parse(fork.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
        var snapshot = await f.Store.ReadAsync(forkId, TestContext.Current.CancellationToken);
        Assert.Equal(1, snapshot.Revision); Assert.Equal("fork-model", snapshot.Settings.GetProperty("model").GetString());
        Assert.Equal("priority", snapshot.Settings.GetProperty("serviceTier").GetString());
        Assert.Equal("friendly", snapshot.Settings.GetProperty("personality").GetString());
        Assert.Equal("test", (await f.Store.ReadAsync(id, TestContext.Current.CancellationToken)).Settings.GetProperty("model").GetString());
        Assert.Equal(2, f.Provider.Requests.Count);
    }

    [Fact]
    public async Task PlanAndModerationComeFromExecutedToolAndProviderMetadata()
    {
        var f = new Fixture { Tools = [CodexTurnPlan.Tool] }; var host = f.Host(); var id = await Create(host);
        f.Provider.Before = n => n == 1 ? "data: {\"type\":\"response.metadata\",\"metadata\":{\"openai_chatgpt_moderation_metadata\":{\"opaque\":\"provider-value\"}}}\n\n"
            : "data: {\"type\":\"response.created\",\"metadata\":{\"openai_chatgpt_moderation_metadata\":{\"wrong-event\":true}}}\n\n";
        var ordinary = f.Provider.Reply;
        f.Provider.Reply = (n, ct) => n == 1 ? Task.FromResult("{\"type\":\"function_call\",\"id\":\"plan_item\",\"call_id\":\"plan_call\",\"name\":\"update_plan\",\"arguments\":\"{\\\"explanation\\\":\\\"progress\\\",\\\"plan\\\":[{\\\"step\\\":\\\"read context\\\",\\\"status\\\":\\\"in_progress\\\"}]}\"}") : ordinary(n, ct);
        await host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.NotificationFailures);
        var plan = Assert.Single(f.Events, e => e.Method == "turn/plan/updated").Params;
        var moderation = Assert.Single(f.Events, e => e.Method == "turn/moderationMetadata").Params;
        Assert.Equal(id.ToString(), plan.GetProperty("threadId").GetString());
        Assert.Equal(plan.GetProperty("turnId").GetString(), moderation.GetProperty("turnId").GetString());
        Assert.Equal("read context", plan.GetProperty("plan")[0].GetProperty("step").GetString());
        Assert.Equal("provider-value", moderation.GetProperty("metadata").GetProperty("opaque").GetString());
        var saved = await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", Assert.Single(saved.Turns[0].Dispatches, d => d.Kind == "tool").State);
        Assert.Equal(2, f.Provider.Requests.Count);
    }

    [Theory]
    [InlineData("{\"plan\":[{\"step\":\"a\",\"status\":\"bad\"}]}")]
    [InlineData("{\"plan\":[{\"step\":\"a\",\"status\":\"in_progress\"},{\"step\":\"b\",\"status\":\"in_progress\"}]}")]
    public async Task InvalidPlanDoesNotPublish(string arguments)
    {
        var published = false;
        await Assert.ThrowsAsync<ArgumentException>(() => CodexTurnPlan.ExecuteAsync(arguments,
            _ => { published = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.False(published);
    }

    [Fact]
    public async Task CatalogPaginationAndReverseAnchorsAreBoundToFilters()
    {
        var f = new Fixture(); var host = f.Host();
        for (var i = 0; i < 5; i++) await Create(host);
        var first = await host.InvokeAsync("thread/list", Json("{\"limit\":2}"), ct: TestContext.Current.CancellationToken);
        var second = await host.InvokeAsync("thread/list", Args(new JsonObject { ["limit"] = 2, ["cursor"] = first.GetProperty("nextCursor").GetString() }), ct: TestContext.Current.CancellationToken);
        Assert.Empty(first.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("id").GetString()).Intersect(second.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("id").GetString())));
        var reverse = await host.InvokeAsync("thread/list", Args(new JsonObject { ["limit"] = 2, ["sortDirection"] = "asc", ["cursor"] = second.GetProperty("backwardsCursor").GetString() }), ct: TestContext.Current.CancellationToken);
        Assert.Equal(second.GetProperty("data")[0].GetProperty("id").GetString(), reverse.GetProperty("data")[0].GetProperty("id").GetString());
        var error = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/list", Args(new JsonObject
        { ["searchTerm"] = "another-filter", ["cursor"] = first.GetProperty("nextCursor").GetString() }), ct: TestContext.Current.CancellationToken));
        Assert.Equal(-32602, error.Code); Assert.Empty(f.Provider.Requests); Assert.Equal(0, f.Budget.Reserves);
        var unassigned = await host.InvokeAsync("thread/list", Json("{\"sectionId\":null}"), TestContext.Current.CancellationToken);
        Assert.Equal(5, unassigned.GetProperty("data").GetArrayLength());
        var assigned = await host.InvokeAsync("thread/list", Json("{\"sectionId\":\"another-section\"}"), TestContext.Current.CancellationToken);
        Assert.Empty(assigned.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task CatalogContinuationKeepsOriginalSortBoundaryWhenAnchorMovesOrDisappears()
    {
        var f = new Fixture(); var host = f.Host();
        for (var i = 0; i < 5; i++) await Create(host);
        var first = await host.InvokeAsync("thread/list", Json("{\"limit\":2,\"sortKey\":\"updated_at\"}"), ct: TestContext.Current.CancellationToken);
        var anchorId = Guid.Parse(first.GetProperty("data")[1].GetProperty("id").GetString() ?? throw new InvalidDataException());
        var anchor = await f.Store.ReadAsync(anchorId, cancellationToken: TestContext.Current.CancellationToken);
        await f.Store.UpdateSettingsAsync(anchorId, anchor.Revision, anchor.Settings, cancellationToken: TestContext.Current.CancellationToken);
        var query = Args(new JsonObject { ["limit"] = 2, ["sortKey"] = "updated_at", ["cursor"] = first.GetProperty("nextCursor").GetString() });
        var afterMove = await host.InvokeAsync("thread/list", query, ct: TestContext.Current.CancellationToken);
        Assert.Empty(first.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("id").GetString())
            .Intersect(afterMove.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("id").GetString())));
        Assert.Equal(2, afterMove.GetProperty("data").GetArrayLength());
        f.Storage.Rows.Remove(anchorId);
        var afterRemoval = await host.InvokeAsync("thread/list", query, ct: TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(afterMove, afterRemoval));
        Assert.Empty(f.Provider.Requests);
    }

    [Fact]
    public async Task ItemHistorySupportsThreadWideAscendingPagesAndExclusiveTurnAnchors()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var first = await host.InvokeAsync("turn/start", Input(id, "first"), ct: TestContext.Current.CancellationToken);
        var firstTurn = first.GetProperty("turn").GetProperty("id").GetString();
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken); f.Completed = Signal();
        await host.InvokeAsync("turn/start", Input(id, "second"), ct: TestContext.Current.CancellationToken);
        await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var page = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString(), ["limit"] = 2 }), ct: TestContext.Current.CancellationToken);
        var data = page.GetProperty("data"); Assert.Equal(2, data.GetArrayLength());
        Assert.All(data.EnumerateArray(), item => Assert.Equal(firstTurn, item.GetProperty("turnId").GetString()));
        Assert.Equal("userMessage", data[0].GetProperty("item").GetProperty("type").GetString());
        var second = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString(),
            ["cursor"] = page.GetProperty("nextCursor").GetString(), ["limit"] = 2 }), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, second.GetProperty("data").GetArrayLength());
        Assert.All(second.GetProperty("data").EnumerateArray(), item => Assert.NotEqual(firstTurn, item.GetProperty("turnId").GetString()));
        var anchored = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString(), ["turnId"] = firstTurn,
            ["cursor"] = new JsonObject { ["type"] = "item", ["itemId"] = data[0].GetProperty("item").GetProperty("id").GetString() } }), ct: TestContext.Current.CancellationToken);
        Assert.Single(anchored.GetProperty("data").EnumerateArray());
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString(),
            ["cursor"] = new JsonObject { ["type"] = "item", ["itemId"] = "missing" } }), ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, host.NotificationFailures);
        Assert.Equal(2, f.Provider.Requests.Count);
    }

    [Fact]
    public async Task StoredReadsDoNotLoadOrExecuteAndPolicyRejectionDoesNotWrite()
    {
        var f = new Fixture(); var id = await Create(f.Host()); var cold = f.Host();
        var read = await cold.InvokeAsync("thread/read", Args(new JsonObject { ["threadId"] = id.ToString(), ["includeTurns"] = false }), ct: TestContext.Current.CancellationToken);
        Assert.Equal("notLoaded", read.GetProperty("thread").GetProperty("status").GetProperty("type").GetString());
        Assert.Empty((await cold.InvokeAsync("thread/loaded/list", Json("{}"), ct: TestContext.Current.CancellationToken)).GetProperty("data").EnumerateArray());
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => cold.InvokeAsync("thread/start", Json("{\"sandbox\":\"danger-full-access\"}"), ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => cold.InvokeAsync("thread/start", Json("{\"ephemeral\":\"true\"}"), ct: TestContext.Current.CancellationToken));
        Assert.Single(f.Storage.Rows); Assert.Empty(f.Provider.Requests);
        await cold.InvokeAsync("thread/resume", Args(new JsonObject { ["threadId"] = id.ToString() }), ct: TestContext.Current.CancellationToken);
        Assert.Single((await cold.InvokeAsync("thread/loaded/list", Json("{}"), ct: TestContext.Current.CancellationToken)).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task SteeringAndLiveSettingsReachNextCaptureWithoutChangingDefaults()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        var requested = Signal(); var release = Signal(); var ordinary = f.Provider.Reply;
        f.Provider.Reply = async (n, ct) => { if (n == 1) { requested.TrySetResult(); await release.Task.WaitAsync(ct); } return await ordinary(n, ct); };
        var initial = (JsonObject)(JsonNode.Parse(Input(id).GetRawText()) ?? throw new InvalidDataException()); initial["model"] = "test-start"; initial["clientUserMessageId"] = "client-first"; initial["turnTrigger"] = "watch";
        var start = await host.InvokeAsync("turn/start", Args(initial), ct: TestContext.Current.CancellationToken);
        var turn = start.GetProperty("turn").GetProperty("id").GetString();
        await requested.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var steering = (JsonObject)(JsonNode.Parse(Input(id, "second").GetRawText()) ?? throw new InvalidDataException()); steering["expectedTurnId"] = turn; steering["clientUserMessageId"] = "client-second";
        var receipt = await host.InvokeAsync("turn/steer", Args(steering), ct: TestContext.Current.CancellationToken); Assert.Equal(turn, receipt.GetProperty("turnId").GetString());
        var update = await host.InvokeAsync("turn/settings/update", Args(new JsonObject { ["threadId"] = id.ToString(), ["turnId"] = turn,
            ["model"] = "test-new", ["effort"] = "high", ["summary"] = "concise", ["serviceTier"] = "priority" }), ct: TestContext.Current.CancellationToken);
        Assert.Equal("applied", update.GetProperty("status").GetString());
        release.TrySetResult(); await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.NotificationFailures);
        Assert.Equal(2, f.Provider.Requests.Count);
        var request = f.Provider.Requests.Last(); Assert.Equal("test-new", request.GetProperty("model").GetString());
        Assert.Equal("priority", request.GetProperty("service_tier").GetString());
        Assert.Equal("concise", request.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Contains("second", request.GetProperty("input").GetRawText());
        var saved = await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal("test-start", saved.Settings.GetProperty("model").GetString());
        Assert.Equal("completed", Assert.Single(saved.Turns).Status);
        Assert.Equal(new string?[] { "client-first", "client-second" }, saved.Turns[0].ClientUserMessageIds);
        Assert.Equal("watch", saved.Turns[0].TurnTrigger);
        Assert.DoesNotContain("client-first", request.GetRawText());
        Assert.Equal(1, f.Events.Count(e => e.Method == "turn/started")); Assert.Equal(1, f.Events.Count(e => e.Method == "turn/completed"));
        Assert.Contains(f.Events, e => e.Method == "item/started"); Assert.Contains(f.Events, e => e.Method == "item/completed");
        var items = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString(), ["turnId"] = turn, ["sortDirection"] = "asc" }), ct: TestContext.Current.CancellationToken);
        Assert.Equal(4, items.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task InterruptProtectsDispatchedToolAndNextTurnStillCompletes()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host); var toolStarted = Signal(); var release = Signal();
        f.Tools = [Json("{\"type\":\"function\",\"name\":\"hold_read\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}")];
        var ordinary = f.Provider.Reply;
        f.Provider.Reply = (n, ct) => n == 1 ? Task.FromResult("{\"type\":\"function_call\",\"id\":\"fc1\",\"call_id\":\"read1\",\"name\":\"hold_read\",\"arguments\":\"{}\"}") : ordinary(n, ct);
        var calls = 0;
        f.Tool = async (_, ct) => { calls++; toolStarted.TrySetResult(); await release.Task.WaitAsync(ct); Assert.False(ct.IsCancellationRequested); return new("known-result"); };
        var started = await host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken); var turn = started.GetProperty("turn").GetProperty("id").GetString();
        await toolStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var interrupt = host.InvokeAsync("turn/interrupt", Args(new JsonObject { ["threadId"] = id.ToString(), ["turnId"] = turn }), ct: TestContext.Current.CancellationToken);
        Assert.False(interrupt.IsCompleted); release.TrySetResult(); await interrupt.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var interrupted = await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal("interrupted", interrupted.Turns[0].Status);
        Assert.Equal("succeeded", Assert.Single(interrupted.Turns[0].Dispatches, d => d.Kind == "tool").State);
        var page = await host.InvokeAsync("thread/items/list", Args(new JsonObject { ["threadId"] = id.ToString(), ["turnId"] = turn }), TestContext.Current.CancellationToken);
        var action = Assert.Single(page.GetProperty("data").EnumerateArray(), entry => entry.GetProperty("item").GetProperty("type").GetString() == "dynamicToolCall");
        Assert.Equal("known-result", action.GetProperty("item").GetProperty("contentItems")[0].GetProperty("text").GetString());
        f.Completed = Signal(); await host.InvokeAsync("turn/start", Input(id, "after interruption"), ct: TestContext.Current.CancellationToken); await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var next = await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal("completed", next.Turns[1].Status); Assert.Equal(1, calls);
        Assert.Equal(0, host.NotificationFailures);
    }

    [Fact]
    public async Task ForkAndHistoryKeepIdentityAndNeverStartProviderWork()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        await host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken); await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.NotificationFailures);
        var before = await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken); var calls = f.Provider.Requests.Count;
        var fork = await host.InvokeAsync("thread/fork", Args(new JsonObject { ["threadId"] = id.ToString(), ["lastTurnId"] = before.Turns[0].Id.ToString() }), ct: TestContext.Current.CancellationToken);
        var forkId = Guid.Parse(fork.GetProperty("thread").GetProperty("id").GetString() ?? throw new InvalidDataException());
        Assert.NotEqual(id, forkId); Assert.Equal(id.ToString(), fork.GetProperty("thread").GetProperty("sessionId").GetString());
        var turns = await host.InvokeAsync("thread/turns/list", Args(new JsonObject { ["threadId"] = forkId.ToString(), ["itemsView"] = "full" }), ct: TestContext.Current.CancellationToken);
        Assert.Single(turns.GetProperty("data").EnumerateArray()); Assert.Equal(calls, f.Provider.Requests.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.ForkAsync(id, before.Revision, Guid.NewGuid(), before.Turns[0].Id, before.Turns[0].Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisconnectedNotificationSubscriberDoesNotReplayCompletedWork()
    {
        var f = new Fixture(); var host = f.Host(brokenNotifications: true); var id = await Create(host);
        await host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken); await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("completed", (await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken)).Turns[0].Status);
        Assert.Single(f.Provider.Requests); Assert.True(host.NotificationFailures > 0);
    }

    [Fact]
    public async Task IndependentTurnsStartTogetherAndHostNotificationsAreIdentityFenced()
    {
        var f = new Fixture(); var host = f.Host(); var one = await Create(host); var two = await Create(host);
        var both = Signal(); var release = Signal(); var ordinary = f.Provider.Reply; var count = 0;
        f.Provider.Reply = async (n, ct) => { if (Interlocked.Increment(ref count) == 2) both.TrySetResult(); await release.Task.WaitAsync(ct); return await ordinary(n, ct); };
        var first = await host.InvokeAsync("turn/start", Input(one), ct: TestContext.Current.CancellationToken);
        var second = await host.InvokeAsync("turn/start", Input(two), ct: TestContext.Current.CancellationToken);
        await both.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var turn = first.GetProperty("turn").GetProperty("id").GetString();
        foreach (var (method, body) in new[]
        {
            ("turn/diff/updated", new JsonObject { ["diff"] = "trusted host diff" }),
            ("turn/plan/updated", new JsonObject { ["explanation"] = null, ["plan"] = new JsonArray() }),
            ("turn/moderationMetadata", new JsonObject { ["metadata"] = new JsonObject { ["opaque"] = true } })
        })
        {
            body["threadId"] = one.ToString(); body["turnId"] = turn;
            await host.PublishAsync(method, Args(body));
            Assert.Contains(f.Events, e => e.Method == method && e.Params.GetProperty("threadId").GetString() == one.ToString());
            body["turnId"] = second.GetProperty("turn").GetProperty("id").GetString();
            await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.PublishAsync(method, Args(body)));
        }
        release.TrySetResult();
        await Task.WhenAll(f.Terminals.GetOrAdd(one.ToString(), _ => Signal()).Task,
            f.Terminals.GetOrAdd(two.ToString(), _ => Signal()).Task).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.NotificationFailures);
    }

    [Fact]
    public async Task ShutdownDrainsActiveTurnAndFencesNewExecution()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host); var requested = Signal();
        f.Provider.Reply = async (_, ct) => { requested.TrySetResult(); await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); return "{}"; };
        await host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken); await requested.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await host.StopAsync(ct: TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.NotificationFailures);
        Assert.Equal("interrupted", (await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken)).Turns.Single().Status);
        var failure = await Assert.ThrowsAsync<CodexThreadRpcException>(() => host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken));
        Assert.Equal(-32010, failure.Code);
        Assert.Single(f.Provider.Requests);
    }

    [Fact]
    public async Task InterruptionAfterJournalAdmissionButBeforeDispatchNeverStartsCallback()
    {
        var f = new Fixture(); var host = f.Host(); var id = await Create(host);
        f.Tools = [Json("{\"type\":\"function\",\"name\":\"hold_read\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}")];
        f.Provider.Reply = (_, _) => Task.FromResult("{\"type\":\"function_call\",\"id\":\"fc_late\",\"call_id\":\"late\",\"name\":\"hold_read\",\"arguments\":\"{}\"}");
        var callbacks = 0;
        f.Tool = (_, _) => { callbacks++; return Task.FromResult(new CodexToolOutput("must not run")); };
        f.Storage.AfterToolAdmission = () => Assert.IsType<CodexAgentControl>(f.LastControl).Interrupt();
        await host.InvokeAsync("turn/start", Input(id), ct: TestContext.Current.CancellationToken); await f.Completed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, host.NotificationFailures);
        var turn = Assert.Single((await f.Store.ReadAsync(id, cancellationToken: TestContext.Current.CancellationToken)).Turns);
        Assert.Equal("interrupted", turn.Status);
        var receipt = Assert.Single(turn.Dispatches, d => d.Kind == "tool");
        Assert.Equal("failed", receipt.State); Assert.True((receipt.Result ?? throw new InvalidDataException()).GetProperty("notDispatched").GetBoolean());
    }
}
