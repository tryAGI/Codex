using System.Text.Json;

namespace tryAGI.Codex.Tests;

/// <summary>
/// Opt-in checks against real Codex endpoints. Skipped unless <c>CODEX_LIVE_TESTS=1</c>.
/// Run them with <c>dotnet test --filter Category=Live</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The App Server test needs a <c>codex</c> executable (<c>CODEX_EXECUTABLE</c>, default <c>codex</c> on <c>PATH</c>).
/// It uses an empty temporary <c>CODEX_HOME</c>, so it never touches a real login.</item>
/// <item>The authenticated tests need <c>CODEX_ACCESS_TOKEN</c> and <c>CODEX_ACCOUNT_ID</c> from a ChatGPT login,
/// and optionally <c>CODEX_MODEL</c>. Only a short-lived access token is used: a refresh token rotates on use,
/// so storing one in CI would break after the first run.</item>
/// </list>
/// </remarks>
[Trait("Category", "Live")]
public sealed class LiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CODEX_LIVE_TESTS") == "1";

    [Fact]
    public async Task AppServerHandshakeAndReadOnlyRequests()
    {
        Assert.SkipUnless(Enabled, "Set CODEX_LIVE_TESTS=1 to run live tests.");
        var ct = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("codex-live-home-").FullName;
        try
        {
            await using var client = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions
            {
                ExecutablePath = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE") ?? "codex",
                CodexHome = home,
                WorkingDirectory = home,
            }, ct);

            Assert.True(client.InitializeResult.TryGetProperty("userAgent", out _));
            var account = await client.RequestAsync("account/read", Json("{}"), ct);
            Assert.True(account.TryGetProperty("requiresOpenaiAuth", out _));
            var threads = await client.RequestAsync("thread/list", Json("{}"), ct);
            Assert.Equal(JsonValueKind.Array, threads.GetProperty("data").ValueKind);

            var error = await Assert.ThrowsAsync<CodexAppServerException>(() => client.RequestAsync("no/such/method", Json("{}"), ct));
            Assert.NotEqual(0, error.Code);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task AgentTurnCallsToolAndAnswers()
    {
        var credentials = Credentials();
        var ct = TestContext.Current.CancellationToken;
        using var client = new CodexClient(credentials);
        var agent = new CodexAgent(client);
        var options = new CodexResponseOptions(Model, "Answer in one short sentence.")
        {
            Tools = [Json("""
                {"type":"function","name":"secret_word","description":"Returns the secret word.",
                 "parameters":{"type":"object","properties":{},"additionalProperties":false},"strict":true}
                """)],
            RequiredFirstFunction = "secret_word",
        };
        var history = new[] { Json("""{"type":"message","role":"user","content":[{"type":"input_text","text":"What is the secret word?"}]}""") };

        var result = await agent.RunAsync(options, history, (_, _) => Task.FromResult("pelican"), cancellationToken: ct);

        Assert.Equal(1, result.ToolCalls);
        Assert.Contains("pelican", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountUsageIsReadable()
    {
        var credentials = Credentials();
        using var client = new CodexClient(credentials);

        var usage = await client.GetAccountUsageAsync(TestContext.Current.CancellationToken);

        Assert.Equal("matched", usage.IdentityValidation);
    }

    private static string Model => Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-6.1-sol";

    private static StaticCredentials Credentials()
    {
        Assert.SkipUnless(Enabled, "Set CODEX_LIVE_TESTS=1 to run live tests.");
        var token = Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");
        var account = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_ID");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(account),
            "Set CODEX_ACCESS_TOKEN and CODEX_ACCOUNT_ID to run authenticated live tests.");
        return new StaticCredentials(new CodexCredentials(token ?? "", account ?? ""));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StaticCredentials(CodexCredentials credentials) : ICodexCredentialProvider
    {
        public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(credentials);
    }
}
