using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// The managed and Rust hosts receive the same synthetic catalog. No provider
// credentials, user files, real inference, or external network are required.
internal sealed class RustThreadModelDifferentialFixture : IAsyncDisposable
{
    private readonly DirectoryInfo root;
    private readonly string executable;
    private readonly string rustRoot;
    private readonly string codexHome;
    private readonly string nativeRoot;
    private readonly Storage storage = new();
    private CodexAppServerClient? rust;
    private CodexThreadRpc? native;
    internal RustSyntheticTurnProvider? SyntheticTurns { get; private set; }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal const string Recommended = "parity-recommended";
    internal static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // Synthetic ModelInfo records, derived from the required Rust fields at
    // the pinned revision. Display order deliberately differs from priority.
    internal static JsonElement Catalog()
    {
        static JsonObject Model(string slug, int priority, string visibility)
        {
            return new JsonObject
            {
                ["slug"] = slug, ["display_name"] = slug, ["description"] = "Synthetic differential model",
                ["base_instructions"] = "Synthetic offline differential fixture. No tool or inference execution.",
                ["default_reasoning_level"] = "medium",
                ["supported_reasoning_levels"] = new JsonArray(new JsonObject
                    { ["effort"] = "medium", ["description"] = "Synthetic reasoning effort" }),
                ["shell_type"] = "shell_command", ["visibility"] = visibility,
                ["supported_in_api"] = true, ["priority"] = priority,
                ["support_verbosity"] = true, ["default_verbosity"] = "low",
                ["truncation_policy"] = new JsonObject { ["limit"] = 10000, ["mode"] = "tokens" },
                ["context_window"] = 100000, ["max_context_window"] = 100000,
                ["experimental_supported_tools"] = new JsonArray(),
                ["input_modalities"] = new JsonArray(JsonValue.Create("text")),
            };
        }
        return Json(new JsonObject
        {
            ["models"] = new JsonArray(Model("parity-later", 3, "list"),
                Model("parity-hidden", 0, "hide"), Model(Recommended, 1, "list"),
                Model("parity-middle", 2, "list"))
        }.ToJsonString());
    }

    private sealed class Storage : ICodexThreadStorage
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, JsonElement> rows = [];
        public Task<JsonElement?> ReadAsync(Guid id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(rows.TryGetValue(id, out var row) ? (JsonElement?)row.Clone() : null);
        }

        public Task<bool> CompareExchangeAsync(Guid id, long? revision, JsonElement body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                long? before = rows.TryGetValue(id, out var row) ? row.GetProperty("revision").GetInt64() : null;
                if (before != revision) return Task.FromResult(false);
                rows[id] = body.Clone();
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<CodexThreadDocument>> ListAsync(CodexThreadStore store, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult<IReadOnlyList<CodexThreadDocument>>(rows.Values.Select(store.ValidateSnapshot).ToArray());
        }
    }

    internal ConcurrentQueue<(string Method, JsonElement Payload)> NativeNotifications { get; } = new();
    internal CodexAppServerClient Rust => rust ?? throw new InvalidOperationException("Rust host not initialized.");
    internal CodexThreadRpc Native => native ?? throw new InvalidOperationException("Native host not initialized.");

    private RustThreadModelDifferentialFixture(DirectoryInfo root, string executable)
    {
        this.root = root;
        this.executable = executable;
        rustRoot = root.CreateSubdirectory("rust").FullName;
        nativeRoot = root.CreateSubdirectory("native").FullName;
        codexHome = root.CreateSubdirectory("codex-home").FullName;
    }

    internal static async Task<RustThreadModelDifferentialFixture> CreateAsync(bool enableSyntheticTurns = false)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL") == "1",
            "Use the explicit isolated Rust differential runner.");
        Assert.True(OperatingSystem.IsLinux(), "The differential lane requires Linux network isolation.");
        var executable = Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL_EXECUTABLE")
            ?? throw new InvalidOperationException("Pinned Rust executable required.");
        Assert.True(Path.IsPathFullyQualified(executable));
        Assert.True(File.Exists(executable));
        var parentNamespace = Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL_PARENT_NETNS")
            ?? throw new InvalidOperationException("Original network namespace required.");
        var currentNamespace = new FileInfo("/proc/self/ns/net").LinkTarget;
        Assert.False(string.IsNullOrWhiteSpace(currentNamespace));
        Assert.NotEqual(parentNamespace, currentNamespace);
        Assert.Equal(new[] { "lo" }, File.ReadAllLines("/proc/net/dev").Skip(2)
            .Select(line => line.Split(':')[0].Trim()).ToArray());

        var root = Directory.CreateTempSubdirectory("codex-rust-thread-model-");
        var f = new RustThreadModelDifferentialFixture(root, executable);
        try
        {
            if (enableSyntheticTurns) f.SyntheticTurns = new RustSyntheticTurnProvider();
            var providerSelection = f.SyntheticTurns is null ? "" : "model_provider = \"mock_provider\"";
            var providerConfig = f.SyntheticTurns is not { } provider ? "" : $"""
                [model_providers.mock_provider]
                name = "Synthetic local fixture"
                base_url = "{provider.BaseUrl}"
                wire_api = "responses"
                request_max_retries = 0
                stream_max_retries = 0
                supports_websockets = false
                """;
            var catalogPath = Path.Combine(f.codexHome, "models.json");
            await File.WriteAllTextAsync(catalogPath, Catalog().GetRawText(), Ct);
            // TOML string quoting is safe here: the generated path is a Linux
            // temp path, serialized by JSON with no untrusted interpolation.
            await File.WriteAllTextAsync(Path.Combine(f.codexHome, "config.toml"), $"""
                model = "{Recommended}"
                model_catalog_json = {JsonSerializer.Serialize(catalogPath)}
                approval_policy = "never"
                sandbox_mode = "read-only"
                cli_auth_credentials_store = "file"
                {providerSelection}

                [analytics]
                enabled = false

                {providerConfig}
                """, Ct);
            await f.RestartAsync();
            return f;
        }
        catch
        {
            await f.DisposeAsync();
            throw;
        }
    }

    internal async Task RestartAsync()
    {
        if (SyntheticTurns is not null && native is not null) await native.StopAsync(Ct);
        if (rust is not null)
        {
            await rust.DisposeAsync();
            rust = null;
        }
        var store = new CodexThreadStore(storage, "synthetic-account");
        var defaults = Json(new JsonObject
        {
            ["model"] = Recommended, ["modelProvider"] = "openai", ["cwd"] = nativeRoot,
            ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["approvalsReviewer"] = "user",
        }.ToJsonString());
        native = new CodexThreadRpc(store, ct => storage.ListAsync(store, ct), defaults,
            (run, started, ct) => SyntheticTurns is { } provider
                ? provider.RunNativeAsync(store, run, started, ct)
                : throw new InvalidOperationException("No provider or turn execution is allowed in this fixture."),
            (method, value) =>
            {
                NativeNotifications.Enqueue((method, value.Clone()));
                return Task.CompletedTask;
            });
        rust = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions
        {
            ExecutablePath = executable, WorkingDirectory = rustRoot, CodexHome = codexHome,
            ClientName = "tryagi_differential", ExperimentalApi = true,
            InitializeTimeout = TimeSpan.FromSeconds(40), MaxOverloadRetries = 0,
        }, Ct);
    }

    internal async Task<(string Native, string Rust)> StartAsync()
    {
        var nativeResult = await Native.InvokeAsync("thread/start", Json("{}"), ct: Ct);
        // Native currently projects paginated history but does not accept the
        // Rust historyMode parameter. This scenario compares behavior only.
        var rustResult = await Rust.RequestAsync("thread/start", new JsonObject
            { ["model"] = Recommended, ["historyMode"] = "paginated" }, Ct)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return (Id(nativeResult), Id(rustResult));
    }

    internal static string Id(JsonElement result) =>
        result.GetProperty("thread").GetProperty("id").GetString()
        ?? throw new InvalidDataException("Thread id missing.");

    internal static JsonObject Params(string id) => new() { ["threadId"] = id };

    internal Task<JsonElement> CallNative(string method, JsonObject parameters) =>
        Native.InvokeAsync(method, Json(parameters.ToJsonString()), ct: Ct);

    internal Task<JsonElement> CallRust(string method, JsonObject parameters) =>
        Rust.RequestAsync(method, parameters, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

    internal async Task<JsonElement> WaitRustNotificationAsync(string method, string threadId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (await Rust.Notifications.WaitToReadAsync(deadline.Token))
            while (Rust.Notifications.TryRead(out var notification))
                if (notification.Method == method && notification.Params is { } payload
                    && payload.TryGetProperty("threadId", out var id) && id.GetString() == threadId)
                    return payload;
        throw new InvalidDataException("Rust notification stream ended before " + method);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (SyntheticTurns is not null && native is not null) await native.StopAsync(CancellationToken.None);
            if (rust is not null) await rust.DisposeAsync();
        }
        finally
        {
            try { if (SyntheticTurns is not null) await SyntheticTurns.DisposeAsync(); }
            finally { root.Delete(recursive: true); }
        }
    }
}

[Trait("Category", "RustDifferential")]
public sealed class RustThreadLifecycleDifferentialTests
{
    private static JsonObject Read(string id) => new() { ["threadId"] = id, ["includeTurns"] = true };
    private static JsonObject Resume(string id) => new() { ["threadId"] = id, ["excludeTurns"] = true };

    private static void AssertEmptyThread(JsonElement result, string id, string? name = null)
    {
        var thread = result.GetProperty("thread");
        Assert.Equal(id, thread.GetProperty("id").GetString());
        Assert.Empty(thread.GetProperty("turns").EnumerateArray());
        Assert.Equal(name, thread.TryGetProperty("name", out var value) ? value.GetString() : null);
    }

    [Fact]
    public async Task NamedEmptyThreadResumesBeforeAndAfterRestart()
    {
        await using var f = await RustThreadModelDifferentialFixture.CreateAsync();
        var ids = await f.StartAsync();
        const string name = "Synthetic scheduled task";
        static JsonObject Rename(string id) => new() { ["threadId"] = id, ["name"] = name };
        Assert.Empty((await f.CallNative("thread/name/set", Rename(ids.Native))).EnumerateObject());
        Assert.Empty((await f.CallRust("thread/name/set", Rename(ids.Rust))).EnumerateObject());
        var nativeEvent = Assert.Single(f.NativeNotifications, x => x.Method == "thread/name/updated").Payload;
        var rustEvent = await f.WaitRustNotificationAsync("thread/name/updated", ids.Rust);
        Assert.Equal(ids.Native, nativeEvent.GetProperty("threadId").GetString());
        Assert.Equal(name, nativeEvent.GetProperty("threadName").GetString());
        Assert.Equal(name, rustEvent.GetProperty("threadName").GetString());

        AssertEmptyThread(await f.CallNative("thread/resume", Resume(ids.Native)), ids.Native, name);
        AssertEmptyThread(await f.CallRust("thread/resume", Resume(ids.Rust)), ids.Rust, name);
        await f.RestartAsync();
        AssertEmptyThread(await f.CallNative("thread/resume", Resume(ids.Native)), ids.Native, name);
        AssertEmptyThread(await f.CallRust("thread/resume", Resume(ids.Rust)), ids.Rust, name);
    }

    [Fact]
    public async Task ArchiveEmptyThreadReadAndRestoreMatchRust()
    {
        await using var f = await RustThreadModelDifferentialFixture.CreateAsync();
        var ids = await f.StartAsync();
        Assert.Empty((await f.CallNative("thread/archive", RustThreadModelDifferentialFixture.Params(ids.Native))).EnumerateObject());
        Assert.Empty((await f.CallRust("thread/archive", RustThreadModelDifferentialFixture.Params(ids.Rust))).EnumerateObject());
        var nativeArchived = Assert.Single(f.NativeNotifications, x => x.Method == "thread/archived").Payload;
        Assert.Equal(ids.Native, nativeArchived.GetProperty("threadId").GetString());
        await f.WaitRustNotificationAsync("thread/archived", ids.Rust);

        var nativeRead = await f.CallNative("thread/read", Read(ids.Native));
        var rustRead = await f.CallRust("thread/read", Read(ids.Rust));
        AssertEmptyThread(nativeRead, ids.Native);
        AssertEmptyThread(rustRead, ids.Rust);
        Assert.Equal("notLoaded", nativeRead.GetProperty("thread").GetProperty("status").GetProperty("type").GetString());
        Assert.Equal("notLoaded", rustRead.GetProperty("thread").GetProperty("status").GetProperty("type").GetString());

        var nativeRestored = await f.CallNative("thread/unarchive", RustThreadModelDifferentialFixture.Params(ids.Native));
        var rustRestored = await f.CallRust("thread/unarchive", RustThreadModelDifferentialFixture.Params(ids.Rust));
        AssertEmptyThread(nativeRestored, ids.Native);
        AssertEmptyThread(rustRestored, ids.Rust);
        Assert.Equal(ids.Native, Assert.Single(f.NativeNotifications, x => x.Method == "thread/unarchived").Payload.GetProperty("threadId").GetString());
        await f.WaitRustNotificationAsync("thread/unarchived", ids.Rust);
        AssertEmptyThread(await f.CallNative("thread/resume", Resume(ids.Native)), ids.Native);
        AssertEmptyThread(await f.CallRust("thread/resume", Resume(ids.Rust)), ids.Rust);
    }
}

[Trait("Category", "RustDifferential")]
public sealed class RustModelCatalogDifferentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static (string? Model, bool Default, bool Hidden)[] Projection(JsonElement result) =>
        result.GetProperty("data").EnumerateArray().Select(row =>
            (row.GetProperty("model").GetString(), row.GetProperty("isDefault").GetBoolean(), row.GetProperty("hidden").GetBoolean())).ToArray();

    [Fact]
    public async Task ExplicitCatalogDisplayOrderDefaultsAndPaginationMatchRust()
    {
        await using var f = await RustThreadModelDifferentialFixture.CreateAsync();
        var source = CodexModelCatalogSource.ForStatic("openai", CodexProviderAuthMode.OpenAiApiKey, "0.172.0",
            RustThreadModelDifferentialFixture.Catalog(), CodexModelCatalogOrigin.ExplicitConfig);
        var native = new CodexModelCatalogRpc([source], "openai");
        var rustFull = Projection(await f.CallRust("model/list", new JsonObject { ["includeHidden"] = true }));
        var nativeFull = Projection(await native.InvokeAsync("model/list", RustThreadModelDifferentialFixture.Json("""{"includeHidden":true}"""), Ct));

        // The fixture must actually be used by both runtimes. Comparing
        // unrelated fallback catalogs would produce meaningless evidence.
        Assert.Equal(4, rustFull.Length);
        Assert.Equal(4, nativeFull.Length);
        Assert.Equal("parity-recommended", Assert.Single(rustFull, x => x.Default).Model);
        Assert.Equal("parity-recommended", Assert.Single(nativeFull, x => x.Default).Model);
        Assert.Equal(rustFull, nativeFull);

        string? cursor = null;
        var nativePaged = new List<(string? Model, bool Default, bool Hidden)>();
        var rustPaged = new List<(string? Model, bool Default, bool Hidden)>();
        for (var i = 0; i < 4; i++)
        {
            var parameters = new JsonObject { ["includeHidden"] = false, ["limit"] = 1 };
            if (cursor is not null) parameters["cursor"] = cursor;
            var rustPage = await f.CallRust("model/list", parameters);
            var nativePage = await native.InvokeAsync("model/list", RustThreadModelDifferentialFixture.Json(parameters.ToJsonString()), Ct);
            var rustNext = rustPage.GetProperty("nextCursor").GetString();
            Assert.Equal(rustNext, nativePage.GetProperty("nextCursor").GetString());
            rustPaged.AddRange(Projection(rustPage)); nativePaged.AddRange(Projection(nativePage));
            cursor = rustNext;
            if (cursor is null) break;
        }
        Assert.Null(cursor);
        Assert.Equal(3, rustPaged.Count);
        Assert.Equal(rustPaged, nativePaged);
    }
}
