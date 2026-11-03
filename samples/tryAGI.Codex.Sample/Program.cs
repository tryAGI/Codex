using System.Text.Json;
using tryAGI.Codex;

// Usage:
//   dotnet run                       sign in with ChatGPT, then run one agent turn natively
//   dotnet run -- --app-server       drive an installed `codex app-server` instead
//   dotnet run -- --live-credentials <dir>
//                                    sign in and write only a short-lived access token and the account ID
//                                    to <dir> for scripts/run-authenticated-live-tests.sh; the refresh token is discarded
var model = Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-6.1-sol";

if (args.Contains("--app-server"))
{
    await RunAppServerAsync();
    return;
}

if (Array.IndexOf(args, "--live-credentials") is var index and >= 0)
{
    await WriteLiveCredentialsAsync(index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("--live-credentials needs a directory."));
    return;
}

// 1. Sign in with the ChatGPT device flow. Store the refresh token securely in a real host.
using var authorization = new CodexDeviceAuthorization();
var code = await authorization.BeginAsync();
Console.WriteLine($"Open {code.VerificationUri} and enter {code.UserCode}");
CodexOAuthTokens? tokens = null;
while (tokens is null)
{
    await Task.Delay(code.PollInterval);
    tokens = await authorization.PollAsync(code);
}

// 2. Run one agent turn with a single tool, streaming text as it arrives.
using var client = new CodexClient(new StaticCredentials(tokens.Credentials));
var agent = new CodexAgent(client);
var options = new CodexResponseOptions(model, "You are a concise assistant.")
{
    Tools = [Json("""
        {"type":"function","name":"utc_now","description":"Returns the current UTC time.",
         "parameters":{"type":"object","properties":{},"additionalProperties":false},"strict":true}
        """)],
};
var history = new[] { Json("""{"type":"message","role":"user","content":[{"type":"input_text","text":"What time is it in UTC?"}]}""") };

var result = await agent.RunAsync(
    options,
    history,
    execute: (call, _) => Task.FromResult(call.Name == "utc_now" ? DateTimeOffset.UtcNow.ToString("O") : "unknown tool"),
    observe: e =>
    {
        if (e.Type == "response.output_text.delta" && e.Payload.TryGetProperty("delta", out var delta))
            Console.Write(delta.GetString());
    });
Console.WriteLine();
Console.WriteLine($"requests={result.Requests} toolCalls={result.ToolCalls}");

async Task RunAppServerAsync()
{
    await using var appServer = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions
    {
        StandardErrorLine = line => Console.Error.WriteLine("codex: " + line),
    });
    Console.WriteLine("Connected: " + appServer.InitializeResult.GetRawText());

    var thread = await appServer.RequestAsync("thread/start", Json("{}"));
    var threadId = thread.GetProperty("thread").GetProperty("id").GetString();
    await appServer.RequestAsync("turn/start", Json($$"""
        {"threadId":"{{threadId}}","input":[{"type":"text","text":"Say hello in one sentence."}]}
        """));

    await foreach (var notification in appServer.ReadNotificationsAsync())
    {
        if (notification.Method == "item/agentMessage/delta" && notification.Params?.TryGetProperty("delta", out var delta) == true)
            Console.Write(delta.GetString());
        if (notification.Method == "turn/completed") break;
    }

    Console.WriteLine();
}

static async Task WriteLiveCredentialsAsync(string directory)
{
    using var authorization = new CodexDeviceAuthorization();
    var code = await authorization.BeginAsync();
    // The prompt goes to stderr so nothing but the credential files is produced.
    Console.Error.WriteLine($"Open {code.VerificationUri} and enter {code.UserCode}");
    CodexOAuthTokens? tokens = null;
    while (tokens is null)
    {
        await Task.Delay(code.PollInterval);
        tokens = await authorization.PollAsync(code);
    }

    Directory.CreateDirectory(directory);
    await WriteSecretAsync(Path.Combine(directory, "access-token"), tokens.AccessToken);
    await WriteSecretAsync(Path.Combine(directory, "account-id"), tokens.AccountId);
    Console.Error.WriteLine($"Signed in; the access token expires at {tokens.ExpiresAt:u}.");
}

static async Task WriteSecretAsync(string path, string value)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    await using var stream = new FileStream(path, options);
    await using var writer = new StreamWriter(stream);
    await writer.WriteAsync(value);
}

static JsonElement Json(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

sealed class StaticCredentials(CodexCredentials credentials) : ICodexCredentialProvider
{
    public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(credentials);
}
