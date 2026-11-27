using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexApprovalLaunchAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly CodexApprovalLaunchScope Scope = new(CodexApprovalAction.Fingerprint("resolved enforcing plan"u8),
        "thread", "turn", "item", "environment");
    private static CodexCommandRequest Request() => new(Guid.NewGuid(), "handle", "connection",
        new[] { "/usr/bin/true", "a b", "" }, Path.GetTempPath(),
        new Dictionary<string, string?> { ["A"] = "", ["B"] = null }, false, false, true,
        null, null, "profile", 4096, TimeSpan.FromSeconds(10));
    private static ValueTask Ignore(CodexCommandStream _, ReadOnlyMemory<byte> __, CancellationToken ___) =>
        ValueTask.CompletedTask;
    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
    private static CodexApprovalAction Identity(CodexCommandRequest request, CodexApprovalLaunchScope? scope = null,
        string method = "command/exec") => CodexApprovalLaunchIdentity.CreateAction(method, request, scope ?? Scope);

    [Fact]
    public void FingerprintBindsEveryLaunchFieldAndEffectivePolicy()
    {
        var r = Request();
        var original = Identity(r);
        Assert.Equal(original, Identity(r with
        {
            Environment = new Dictionary<string, string?> { ["B"] = null, ["A"] = "" },
            Command = r.Command.ToArray(),
        }));
        var changes = new[]
        {
            r with { ExecutionId = Guid.NewGuid() },
            r with { ProcessId = null },
            r with { ConnectionId = "foreign" },
            r with { Command = new[] { "/usr/bin/true", "a", "b", "" } },
            r with { Cwd = null },
            r with { Environment = new Dictionary<string, string?> { ["A"] = null, ["B"] = "" } },
            r with { Tty = true },
            r with { StreamStdin = true },
            r with { StreamStdoutStderr = false },
            r with { Size = new(20, 80) },
            r with { SandboxPolicy = Json("""{"type":"readOnly"}"""), PermissionProfile = null },
            r with { PermissionProfile = "another-profile" },
            r with { OutputBytesCap = 4097 },
            r with { Timeout = TimeSpan.FromSeconds(11) },
        };
        Assert.All(changes, change => Assert.NotEqual(original.ActionSha256, Identity(change).ActionSha256));
        Assert.NotEqual(original.ActionSha256, Identity(r, method: "process/spawn").ActionSha256);
        Assert.NotEqual(original.ActionSha256, Identity(r,
            Scope with { EffectivePolicySha256 = CodexApprovalAction.Fingerprint("other policy"u8) }).ActionSha256);
        Assert.NotEqual(original.ActionSha256, Identity(r, Scope with { EnvironmentId = "other" }).ActionSha256);
    }

    [Fact]
    public async Task FrozenRequestCannotChangeBetweenReviewAndLaunch()
    {
        var f = new Fixture();
        var request = Request();
        var argv = (string[])request.Command;
        var env = (Dictionary<string, string?>)request.Environment;
        var id = Guid.NewGuid();
        var expected = Identity(request);
        await f.Approve(id, expected);
        var calls = 0;
        var adapter = new CodexApprovalCommandAdapter(f.Dispatcher, (snapshot, _) =>
        {
            argv[1] = "changed";
            env["A"] = "changed";
            Assert.Equal(expected, Identity(snapshot));
            return ValueTask.FromResult(new CodexApprovedLaunch<ICodexCommandSession>(id, Scope,
                (exact, receipt, _, _) =>
                {
                    Assert.Equal(expected, Identity(exact));
                    Assert.Equal(expected, receipt.Action);
                    Assert.Equal(CodexApprovalState.Consumed, receipt.State);
                    Assert.Equal("a b", exact.Command[1]);
                    Assert.Equal("", exact.Environment["A"]);
                    calls++;
                    return Task.FromResult<ICodexCommandSession>(new CommandSession());
                }));
        });
        await using var session = await adapter.StartAsync(request, Ignore, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(CodexApprovalState.Consumed, (await f.Store.ReadAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task CommandApprovalCannotStartProcessOrDifferentPolicyOrAttempt()
    {
        var f = new Fixture();
        var request = Request();
        var id = Guid.NewGuid();
        await f.Approve(id, Identity(request));
        var calls = 0;
        var command = new CodexApprovalCommandAdapter(f.Dispatcher, (_, _) =>
            ValueTask.FromResult(new CodexApprovedLaunch<ICodexCommandSession>(id, Scope,
                (_, _, _, _) => { calls++; return Task.FromResult<ICodexCommandSession>(new CommandSession()); })));
        var process = new CodexApprovalProcessAdapter(f.Dispatcher, (_, _) =>
            ValueTask.FromResult(new CodexApprovedLaunch<ICodexProcessSession>(id, Scope,
                (_, _, _, _) => { calls++; return Task.FromResult<ICodexProcessSession>(new ProcessSession()); })));
        var changed = new CodexApprovalCommandAdapter(f.Dispatcher, (_, _) =>
            ValueTask.FromResult(new CodexApprovedLaunch<ICodexCommandSession>(id,
                Scope with { EffectivePolicySha256 = CodexApprovalAction.Fingerprint("different plan"u8) },
                (_, _, _, _) => { calls++; return Task.FromResult<ICodexCommandSession>(new CommandSession()); })));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => process.StartAsync(request, Ignore, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => changed.StartAsync(request, Ignore, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => command.StartAsync(request with { ExecutionId = Guid.NewGuid() }, Ignore, Ct));
        Assert.Equal(0, calls);
        Assert.Equal(CodexApprovalState.Approved, (await f.Store.ReadAsync(id, Ct))!.State);
        await using var session = await command.StartAsync(request, Ignore, Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => command.StartAsync(request, Ignore, Ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RevocationAfterConsumePreventsLaunchWithoutResurrectingGrant()
    {
        var f = new Fixture();
        var request = Request();
        var id = Guid.NewGuid();
        await f.Approve(id, Identity(request));
        var checks = 0;
        var dispatcher = new CodexApprovalDispatcher(f.Store, (_, _) =>
            ++checks == 2 ? throw new UnauthorizedAccessException("Policy changed.") : ValueTask.CompletedTask);
        var adapter = new CodexApprovalCommandAdapter(dispatcher, (_, _) =>
            ValueTask.FromResult(new CodexApprovedLaunch<ICodexCommandSession>(id, Scope,
                (_, _, _, _) => throw new InvalidOperationException("Must not launch."))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.StartAsync(request, Ignore, Ct));
        Assert.Equal(2, checks);
        Assert.Equal(CodexApprovalState.Consumed, (await f.Store.ReadAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task ExpiryDuringPostConsumePolicyCheckPreventsLaunch()
    {
        var clock = new Clock();
        var store = new CodexApprovalStore(new Storage(), "account", Authorize, clock);
        var request = Request();
        var action = Identity(request);
        var id = Guid.NewGuid();
        var expires = clock.Now.AddSeconds(10);
        await store.RequestAsync(id, action, expires, Ct);
        await store.DecideAsync(id, action, 1, true, "reviewer", Ct);
        var checks = 0;
        var dispatcher = new CodexApprovalDispatcher(store, (_, _) =>
        {
            if (++checks == 2) clock.Now = expires;
            return ValueTask.CompletedTask;
        });
        var adapter = new CodexApprovalCommandAdapter(dispatcher, (_, _) =>
            ValueTask.FromResult(new CodexApprovedLaunch<ICodexCommandSession>(id, Scope,
                (_, _, _, _) => throw new InvalidOperationException("Expired approval must not launch."))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.StartAsync(request, Ignore, Ct));
        Assert.Equal(CodexApprovalState.Consumed, (await store.ReadAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task InteractiveLaunchDoesNotImplicitlyAuthorizeArbitraryStdin()
    {
        var f = new Fixture();
        var adapter = new CodexApprovalCommandAdapter(f.Dispatcher, (_, _) =>
            throw new InvalidOperationException("Interactive approval must not be requested."));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => adapter.StartAsync(
            Request() with { StreamStdin = true }, Ignore, Ct));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => adapter.StartAsync(
            Request() with { Tty = true }, Ignore, Ct));
    }

    [Fact]
    public async Task AmbiguousConsumedCommitNeverCallsTheLaunchAdapter()
    {
        var f = new Fixture();
        var request = Request();
        var id = Guid.NewGuid();
        await f.Approve(id, Identity(request));
        f.Storage.ThrowOnConsume = true;
        var adapter = new CodexApprovalCommandAdapter(f.Dispatcher, (_, _) =>
            ValueTask.FromResult(new CodexApprovedLaunch<ICodexCommandSession>(id, Scope,
                (_, _, _, _) => throw new InvalidOperationException("Must not launch."))));
        await Assert.ThrowsAsync<IOException>(() => adapter.StartAsync(request, Ignore, Ct));
        Assert.Equal(CodexApprovalState.Consumed, (await f.Store.ReadAsync(id, Ct))!.State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.StartAsync(request, Ignore, Ct));
    }

    [Fact]
    public async Task NativeCommandRpcUsesRealDurableApprovalBeforeSeatbeltLaunch()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Actual Seatbelt and SQLite require macOS.");
        var outer = CodexFileSystem.Canonical(Directory.CreateTempSubdirectory("codex-approval-launch-").FullName);
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(outer, "workspace")).FullName;
            var marker = Path.Combine(workspace, "marker");
            var db = Path.Combine(outer, "approval.db");
            await using var storage = await CodexMacOsSqliteApprovalStorage.OpenAsync(db, createNew: true, cancellationToken: Ct);
            var store = new CodexApprovalStore(storage, "account", Authorize);
            var approvalId = Guid.Empty;
            var bridge = new CodexApprovalRpcBridge(store, "connection");
            var policy = new CodexMacOsLeafPolicy(workspace, true);
            var scope = new CodexApprovalLaunchScope(CodexApprovalAction.Fingerprint(
                JsonSerializer.SerializeToUtf8Bytes(new { adapter = "macos-seatbelt-leaf-v1", policy, environment = "cleared" })),
                "thread", "turn", "item");
            var started = 0;
            var reviews = 0;
            var adapter = new CodexApprovalCommandAdapter(new(store, (_, _) => ValueTask.CompletedTask),
                async (request, ct) =>
                {
                    var action = Identity(request, scope);
                    if (++reviews == 1)
                    {
                        const string approvalMethod = "item/commandExecution/requestApproval";
                        var serverRequestId = JsonSerializer.SerializeToElement(request.ExecutionId.ToString("D"));
                        var prompt = JsonSerializer.SerializeToElement(new
                        {
                            threadId = scope.ThreadId,
                            turnId = scope.TurnId,
                            itemId = scope.ItemId,
                            startedAtMs = 1,
                            command = request.Command[0],
                            availableDecisions = new[] { "accept", "decline" },
                        });
                        var admitted = await bridge.AdmitAsync(serverRequestId, approvalMethod, prompt, action,
                            DateTimeOffset.UtcNow.AddMinutes(5), ct);
                        Assert.True(admitted.Created);
                        approvalId = admitted.Record.Id;
                        var result = await bridge.RespondAsync(serverRequestId, approvalMethod, action,
                            admitted.Record.Revision, "reviewer", Json("""{"decision":"accept"}"""), ct);
                        Assert.Equal(CodexApprovalState.Approved, result.Record.State);
                    }
                    return new(approvalId, scope, async (exact, receipt, output, token) =>
                    {
                        Assert.Equal(action, receipt.Action);
                        Assert.Equal(CodexApprovalState.Consumed, (await store.ReadAsync(approvalId, token))!.State);
                        var contained = new CodexMacOsLeafCommandAdapter((launch, _) =>
                        {
                            Assert.Equal(action, Identity(launch, scope));
                            Assert.True(DateTimeOffset.UtcNow < receipt.ExpiresAt);
                            return ValueTask.FromResult(policy);
                        });
                        started++;
                        return await contained.StartAsync(exact, output, token);
                    });
                });
            await using var host = new CodexCommandExecution("connection", adapter, (_, _, _) => ValueTask.CompletedTask);
            var parameters = JsonSerializer.SerializeToElement(new
            {
                command = new[] { "/usr/bin/touch", marker },
                cwd = workspace,
                permissionProfile = CodexMacOsLeafCommandAdapter.WorkspaceProfile,
            });
            var result = await host.HandleAsync("command/exec", parameters, Ct);
            Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
            Assert.True(File.Exists(marker));
            File.Delete(marker);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.HandleAsync("command/exec", parameters, Ct));
            Assert.False(File.Exists(marker));
            Assert.Equal(1, started);
            Assert.Equal(2, reviews);
        }
        finally { Directory.Delete(outer, true); }
    }

    [Fact]
    public async Task ProcessRpcRequiresApprovalBeforeAcknowledgementAndPreservesLifecycle()
    {
        var f = new Fixture();
        var id = Guid.NewGuid();
        var approvals = 0;
        var starts = 0;
        var acknowledged = 0;
        var notifications = Channel.CreateUnbounded<string>();
        var adapter = new CodexApprovalProcessAdapter(f.Dispatcher, async (request, ct) =>
        {
            var action = Identity(request, method: "process/spawn");
            if (++approvals == 1) await f.Approve(id, action, ct);
            return new(id, Scope, (_, receipt, _, _) =>
            {
                Assert.Equal(action, receipt.Action);
                starts++;
                return Task.FromResult<ICodexProcessSession>(new ProcessSession());
            });
        });
        await using var host = new CodexProcessExecution("connection", adapter,
            (method, _, _) => { notifications.Writer.TryWrite(method); return ValueTask.CompletedTask; },
            (_, _) => throw new InvalidOperationException("Unexpected failure."));
        var parameters = JsonSerializer.SerializeToElement(new
        {
            command = new[] { "fixture" }, processHandle = "client-handle", cwd = Path.GetTempPath(),
        });
        ValueTask Ack(JsonElement _, CancellationToken __)
        {
            Assert.Equal(1, starts);
            acknowledged++;
            return ValueTask.CompletedTask;
        }
        await host.HandleAsync("process/spawn", parameters, Ack, Ct);
        Assert.Equal("process/exited", await notifications.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await host.DisposeAsync();
        await using var reattached = new CodexProcessExecution("connection", adapter,
            (_, _, _) => throw new InvalidOperationException("Rejected launch cannot notify."),
            (_, _) => throw new InvalidOperationException("Unexpected failure."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reattached.HandleAsync("process/spawn", parameters, Ack, Ct));
        Assert.Equal(1, acknowledged);
        Assert.Equal(1, starts);
    }

    private static ValueTask Authorize(CodexApprovalAuthorization request, CancellationToken _) =>
        request.ActorId == "reviewer" ? ValueTask.CompletedTask : throw new UnauthorizedAccessException();

    private sealed class Fixture
    {
        internal Storage Storage { get; } = new();
        internal CodexApprovalStore Store { get; }
        internal CodexApprovalDispatcher Dispatcher { get; }
        internal Fixture()
        {
            Store = new(Storage, "account", Authorize);
            Dispatcher = new(Store, (_, _) => ValueTask.CompletedTask);
        }
        internal async Task Approve(Guid id, CodexApprovalAction action, CancellationToken? ct = null)
        {
            var token = ct ?? Ct;
            var pending = await Store.RequestAsync(id, action, DateTimeOffset.UtcNow.AddMinutes(5), token);
            await Store.DecideAsync(id, action, pending.Revision, true, "reviewer", token);
        }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Storage : ICodexApprovalStorage
    {
        private readonly Dictionary<(string, Guid), CodexApprovalRecord> records = new();
        private readonly object gate = new();
        internal bool ThrowOnConsume { get; set; }
        public Task<CodexApprovalRecord?> ReadAsync(string accountId, Guid requestId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(records.GetValueOrDefault((accountId, requestId)));
        }
        public Task<bool> CompareExchangeAsync(string accountId, Guid requestId, long? expectedRevision,
            CodexApprovalRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                var key = (accountId, requestId);
                if (records.GetValueOrDefault(key)?.Revision != expectedRevision) return Task.FromResult(false);
                records[key] = record;
                if (ThrowOnConsume && record.State == CodexApprovalState.Consumed)
                    throw new IOException("Commit acknowledgement lost.");
                return Task.FromResult(true);
            }
        }
    }

    private sealed class CommandSession : ICodexCommandSession
    {
        public Task<int> Completion => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask TerminateAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(CodexCommandTerminalSize size, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, bool closeStdin, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ProcessSession : ICodexProcessSession
    {
        public Task<CodexProcessExit> Completion => Task.FromResult(new CodexProcessExit(0, false));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask TerminateAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(CodexCommandTerminalSize size, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, bool closeStdin, CancellationToken ct) => throw new NotSupportedException();
    }
}
