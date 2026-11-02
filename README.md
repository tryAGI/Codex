# tryAGI.Codex

[![Nuget package](https://img.shields.io/nuget/vpre/tryAGI.Codex)](https://www.nuget.org/packages/tryAGI.Codex/)
[![dotnet](https://github.com/tryAGI/Codex/actions/workflows/dotnet.yml/badge.svg?branch=main)](https://github.com/tryAGI/Codex/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/github/license/tryAGI/Codex)](https://github.com/tryAGI/Codex/blob/main/LICENSE)
[![Discord](https://img.shields.io/discord/1115206893015662663?label=Discord&logo=discord&logoColor=white&color=d82679)](https://discord.gg/Ca2xhfBf3v)

A native C# client for the OpenAI Codex protocols, signed in with a ChatGPT account.

**It runs the Codex protocol in-process.** No `codex` executable, App Server process, Node.js or
remote worker is needed: the library speaks the same network contracts the official Rust client
uses. If you already run Codex, an optional `CodexAppServerClient` drives an installed
`codex app-server` over JSON-RPC too.

> **Preview.** These are internal Codex contracts and they change between Codex releases. The
> package is `0.x` and its API may change. See [Protocol versions](#protocol-versions).

### Features

- **Sign-in** — ChatGPT device authorization and token refresh (`CodexDeviceAuthorization`).
  The library never reads or copies another client's credential store.
- **Agent loop** — Codex Responses streaming with caller-defined function and freeform (custom)
  tools, encrypted-reasoning continuation and bounded tool concurrency (`CodexAgent`).
- **Threads** — durable thread/turn state, compaction checkpoints and a native implementation of
  the App Server thread/turn RPC surface (`CodexThreadStore`, `CodexThreadAgent`, `CodexThreadRpc`).
- **Realtime voice** — Codex realtime V3 calls (WebRTC SDP + sideband WebSocket) and a direct
  audio WebSocket session; the GA realtime protocol is also supported (`CodexClient`,
  `CodexRealtimeConnection`).
- **Patches** — the Codex `apply_patch` grammar (streaming parser, fuzzy context lookup) and a
  whole-workspace plan with byte-hash preconditions (`CodexPatch`, `CodexWorkspacePatch`).
  These are pure text operations; nothing is written to disk.
- **File system protocol** — the App Server `fs/*` methods with an explicitly bound root.
- **Usage** — account rate-limit windows, credits and per-request usage observation.
- **App Server client** — JSON-RPC over stdio, the Unix-socket proxy or WebSocket
  (`CodexAppServerClient`).

The host stays in charge: you define the tools, approvals, credential storage, reconnection and
persistence. There is no built-in shell, browsing, sandboxing or fallback to paid API-key inference.

## Installation

```bash
dotnet add package tryAGI.Codex --prerelease
```

Requires .NET 10. The only dependency is [Tomlyn](https://github.com/xoofx/Tomlyn) (Codex `config.toml` layers).

## Quick start

### Sign in

```csharp
using tryAGI.Codex;

using var authorization = new CodexDeviceAuthorization();
var code = await authorization.BeginAsync();
Console.WriteLine($"Open {code.VerificationUri} and enter {code.UserCode}");

CodexOAuthTokens? tokens = null;
while (tokens is null)
{
    await Task.Delay(code.PollInterval);
    tokens = await authorization.PollAsync(code);
}

// Persist tokens securely; refresh before ExpiresAt:
// tokens = await authorization.RefreshAsync(tokens);
```

`CodexClient` asks an `ICodexCredentialProvider` for credentials before every request, so your
provider is where refresh and secure storage live:

```csharp
sealed class TokenStore(CodexOAuthTokens tokens) : ICodexCredentialProvider
{
    public ValueTask<CodexCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(tokens.Credentials);
}

using var client = new CodexClient(new TokenStore(tokens));
```

### Run a turn and stream events

```csharp
using System.Text.Json;

var agent = new CodexAgent(client);
var options = new CodexResponseOptions("gpt-6.1-sol", "You are a concise assistant.")
{
    Tools = [JsonDocument.Parse("""
        {"type":"function","name":"utc_now","description":"Returns the current UTC time.",
         "parameters":{"type":"object","properties":{},"additionalProperties":false},"strict":true}
        """).RootElement],
};
var history = new[]
{
    JsonDocument.Parse("""{"type":"message","role":"user","content":[{"type":"input_text","text":"What time is it in UTC?"}]}""").RootElement,
};

var result = await agent.RunAsync(
    options,
    history,
    execute: (call, ct) => Task.FromResult(DateTimeOffset.UtcNow.ToString("O")),
    observe: e =>
    {
        if (e.Type == "response.output_text.delta")
            Console.Write(e.Payload.GetProperty("delta").GetString());
    });

// result.History holds every output item, including encrypted reasoning:
// pass it back as the next turn's history.
```

Use a model your ChatGPT plan offers. For lower-level control, `client.StreamResponseAsync(...)`
yields every raw Responses event of a single request.

### Start a thread

`CodexThreadStore` keeps thread documents behind an `ICodexThreadStorage` you implement (a database,
files, …). `CodexThreadAgent` runs a turn against a stored thread with optimistic revisions, and
`CodexThreadRpc` serves the App Server thread/turn methods (`thread/start`, `turn/start`,
`turn/interrupt`, …) on top of them, so an App Server client can talk to your host without a
Codex process.

### Realtime session

```csharp
var session = new CodexRealtimeSessionOptions("You are a helpful voice assistant.");

// Direct audio WebSocket: you supply PCM16 input and play the output.
await using var realtime = await client.ConnectRealtimeWebSocketAsync(session);
await realtime.AppendUserTextAsync("Hello!");
await foreach (var e in realtime.ReadEventsAsync())
{
    if (e.Transcript is { Final: true } t) Console.WriteLine($"{t.Role}: {t.Text}");
    if (e.Delegation is { } delegation) { /* run the delegated task, then AppendContextAsync(...) */ }
}
```

For WebRTC, create the call with `CreateRealtimeCallAsync(offerSdp, session)`, hand the returned
SDP answer to your media stack, then `ConnectSidebandAsync(call.CallId)` for the control channel.
The media stack is intentionally yours.

Read sideband events promptly, dispatch delegations separately, deduplicate delegation IDs
durably before acting, and keep every response output item in history. A failed call creation
can be ambiguous and is never retried automatically.

### Optional: drive an installed App Server

```csharp
await using var appServer = await CodexAppServerClient.ConnectAsync(new CodexAppServerOptions
{
    Endpoint = "stdio://",               // or unix://, ws://127.0.0.1:PORT, wss://host/path
    ServerRequestHandler = (request, ct) => // approvals and user-input requests
        ValueTask.FromResult(JsonDocument.Parse("""{"decision":"decline"}""").RootElement),
});

var thread = await appServer.RequestAsync("thread/start", JsonDocument.Parse("{}").RootElement);
var threadId = thread.GetProperty("thread").GetProperty("id").GetString();

var turn = new JsonObject
{
    ["threadId"] = threadId,
    ["input"] = CodexUserInput.ToJsonArray([
        new CodexTextInput("What is in this screenshot?"),
        CodexImageInput.FromBytes(File.ReadAllBytes("screen.png"), "image/png"),
    ]),
};
await appServer.RequestAsync("turn/start", JsonDocument.Parse(turn.ToJsonString()).RootElement);
// ... then read appServer.ReadNotificationsAsync() until "turn/completed".
```

Envelopes are raw JSON, so new fields and notifications pass through. Overloaded requests
(`-32001`) are retried with backoff; plain `ws://` is accepted only for loopback hosts.

`CodexUserInput` models every `UserInput` variant (`text`, `image`, `localImage`, `audio`,
`localAudio`, `skill`, `mention`) and writes only the members a variant declares. `Parse`
reads them back, and a variant newer than this package is kept verbatim as `CodexUnknownInput`.

A runnable version of these snippets is in [`samples/tryAGI.Codex.Sample`](samples/tryAGI.Codex.Sample).

## Protocol versions

| Surface | Checked against |
|---------|-----------------|
| Realtime catalog (models, voices) | `openai/codex` `rust-v0.165.0` (`ProtocolBaseline.json`) |
| Realtime V3, Responses, compaction, tools, device sign-in, `apply_patch` | `openai/codex` `rust-v0.160.0` source review ([provenance](docs/UPSTREAM.md)) |
| Native thread/turn RPC (`CodexThreadRpc`) | App Server protocol `0.162.1` (schema-validated in tests) |
| `CodexAppServerClient`, `CodexAppServerProtocol` | App Server protocol <!-- app-server-version -->0.166.0<!-- /app-server-version --> (method catalog packaged in the library) |

`CodexAppServerProtocol` lists every request and notification method of that App Server version, so
a host can check a method name before sending it.

The schemas are in [`specs/codex-app-server`](specs/codex-app-server). Regenerate one for your
installed version with `codex app-server generate-json-schema --experimental --out <dir>`.

### Staying current

A scheduled workflow checks the newest stable `@openai/codex` release every day. When a new App Server
version appears, it adds the schema snapshot, regenerates the packaged method catalog and opens a pull
request. The tests check that every method the library serves or sends still exists in the new
version; when they pass, the pull request merges automatically and a patch release is published.
A version that removes or renames a method this library depends on fails the tests and waits for a human.

## Running the tests

```bash
dotnet test src/tests/tryAGI.Codex.Tests/tryAGI.Codex.Tests.csproj
```

Tests in the `Live` category are skipped unless `CODEX_LIVE_TESTS=1`. They start a real
`codex app-server` (set `CODEX_EXECUTABLE` if it is not on `PATH`) with an isolated `CODEX_HOME`;
the authenticated agent and usage checks also need `CODEX_ACCESS_TOKEN` and `CODEX_ACCOUNT_ID`, and
`CODEX_MODEL` overrides the model they use.

## Support

Priority place for bugs: https://github.com/tryAGI/Codex/issues
Priority place for ideas and general questions: https://github.com/tryAGI/Codex/discussions
Discord: https://discord.gg/Ca2xhfBf3v

## License

MIT. OpenAI Codex is licensed under Apache-2.0; this library is independently written from the
published protocol and source, and copies no upstream prompts, policies or credentials. The JSON
schemas under `specs/` are generated by the Codex CLI. "Codex" and "ChatGPT" are trademarks of
OpenAI; this project is not affiliated with OpenAI.
