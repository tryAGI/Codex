using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

// Mirrors the pinned Rust provider-backed model/list ordering fixture. The
// synthetic account exists only inside a fresh network-isolated test home.
// The managed path receives the same bytes via an injected HTTP handler.
[Trait("Category", "RustDifferential")]
public sealed class RustProviderModelDifferentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Version = "0.172.0";
    private const string Recommended = RustThreadModelDifferentialFixture.Recommended;

    private static string SyntheticCatalog()
    {
        var catalog = RustThreadModelDifferentialFixture.Catalog();
        var models = catalog.GetProperty("models").EnumerateArray().ToDictionary(
            item => item.GetProperty("slug").GetString()!, item => item);
        return new JsonObject
        {
            ["models"] = new JsonArray(
                JsonNode.Parse(models["parity-later"].GetRawText()),
                JsonNode.Parse(models["parity-middle"].GetRawText()),
                JsonNode.Parse(models[Recommended].GetRawText())),
        }.ToJsonString();
    }

    private static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WriteSyntheticAuthAsync(string home)
    {
        // Same synthetic, unsigned ID-token shape as the pinned Rust test
        // fixture. No real credential is read or used by either runtime.
        var token = B64("""{"alg":"none","typ":"JWT"}""") + "." +
            B64("""{"https://api.openai.com/auth":{"chatgpt_plan_type":"team"}}""") + "." +
            B64("synthetic-signature");
        var auth = new JsonObject
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = token,
                ["access_token"] = "synthetic-loopback-token",
                ["refresh_token"] = "synthetic-never-refresh",
            },
            ["last_refresh"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), auth.ToJsonString(), Ct);
    }

    private sealed class Provider(string catalog) : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource lifetime = new();
        private Task? serving;
        private int requests;
        internal int Requests => Volatile.Read(ref requests);
        internal string BaseUrl { get; private set; } = "";

        internal void Start()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/backend-api/codex";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var ctx = await listener.GetContextAsync().WaitAsync(lifetime.Token);
                    try
                    {
                        if (ctx.Request.HttpMethod != "GET" || ctx.Request.Url?.AbsolutePath != "/backend-api/codex/models")
                        {
                            ctx.Response.StatusCode = 404;
                            continue;
                        }
                        if (Interlocked.Increment(ref requests) > 16)
                        {
                            ctx.Response.StatusCode = 429;
                            continue;
                        }
                        var bytes = Encoding.UTF8.GetBytes(catalog);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, lifetime.Token);
                    }
                    finally { ctx.Response.Close(); }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (HttpListenerException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Close();
            try { if (serving is not null) await serving; }
            finally { lifetime.Dispose(); }
        }
    }

    private sealed class Credentials : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CodexCredentials("synthetic-never-transmitted", "synthetic-account")
            {
                ChatGptUserId = "synthetic-user",
            });
        }
    }

    private sealed class NativeHandler(string catalog) : HttpMessageHandler
    {
        private int requests;
        internal int Requests => Volatile.Read(ref requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("chatgpt.com", request.RequestUri?.Host);
            Assert.Equal("/backend-api/codex/models", request.RequestUri?.AbsolutePath);
            Assert.Equal("?client_version=" + Version, request.RequestUri?.Query);
            Assert.InRange(Interlocked.Increment(ref requests), 1, 16);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(catalog, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (string? Model, bool Default)[] Projection(JsonElement result) =>
        result.GetProperty("data").EnumerateArray().Select(item =>
            (item.GetProperty("model").GetString(), item.GetProperty("isDefault").GetBoolean())).ToArray();

    [Fact]
    public async Task ProviderDisplayOrderIsIndependentOfPriorityDefaultAndPagination()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODEX_RUST_DIFFERENTIAL") == "1",
            "Use the explicit isolated Rust differential runner.");
        Assert.True(OperatingSystem.IsLinux());
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

        var root = Directory.CreateTempSubdirectory("codex-provider-catalog-");
        try
        {
            var home = root.CreateSubdirectory("codex-home").FullName;
            var cwd = root.CreateSubdirectory("workspace").FullName;
            var catalog = SyntheticCatalog();
            await using var provider = new Provider(catalog);
            provider.Start();
            await WriteSyntheticAuthAsync(home);
            await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), $"""
                openai_base_url = "{provider.BaseUrl}"
                cli_auth_credentials_store = "file"
                approval_policy = "never"
                sandbox_mode = "read-only"

                [analytics]
                enabled = false
                """, Ct);
            await using var rust = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions
            {
                ExecutablePath = executable, WorkingDirectory = cwd, CodexHome = home,
                ClientName = "tryagi_differential", ExperimentalApi = true,
                InitializeTimeout = TimeSpan.FromSeconds(40), MaxOverloadRetries = 0,
            }, Ct);

            var handler = new NativeHandler(catalog);
            using var client = new CodexClient(new Credentials(), handler: handler);
            var source = new CodexModelCatalogSource("openai", CodexProviderAuthMode.ChatGptSubscription,
                ct => client.GetModelCatalogAsync(Version, cancellationToken: ct), CodexModelCatalogOrigin.Provider);
            var native = new CodexModelCatalogRpc([source], "openai");
            var rustFull = Projection(await rust.RequestAsync("model/list", new JsonObject(), Ct)
                .WaitAsync(TimeSpan.FromSeconds(30), Ct));
            var nativeFull = Projection(await native.InvokeAsync("model/list", RustThreadModelDifferentialFixture.Json("{}"), Ct));

            // Verify the real Rust result and provider observation independently:
            // priority determines the default, not the provider's display order.
            (string? Model, bool Default)[] expected =
            [
                ("parity-later", false), ("parity-middle", false), (Recommended, true),
            ];
            Assert.Equal(expected, rustFull);
            Assert.InRange(provider.Requests, 1, 16);
            Assert.InRange(handler.Requests, 1, 16);
            Assert.Equal(rustFull, nativeFull);

            string? cursor = null;
            var rustPaged = new List<(string? Model, bool Default)>();
            var nativePaged = new List<(string? Model, bool Default)>();
            for (var i = 0; i < expected.Length; i++)
            {
                var parameters = new JsonObject { ["limit"] = 1 };
                if (cursor is not null) parameters["cursor"] = cursor;
                var rustPage = await rust.RequestAsync("model/list", parameters, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
                var nativePage = await native.InvokeAsync("model/list",
                    RustThreadModelDifferentialFixture.Json(parameters.ToJsonString()), Ct);
                cursor = rustPage.GetProperty("nextCursor").GetString();
                Assert.Equal(cursor, nativePage.GetProperty("nextCursor").GetString());
                rustPaged.AddRange(Projection(rustPage));
                nativePaged.AddRange(Projection(nativePage));
            }
            Assert.Null(cursor);
            Assert.Equal(expected, rustPaged);
            Assert.Equal(expected, nativePaged);
        }
        finally { root.Delete(recursive: true); }
    }
}
