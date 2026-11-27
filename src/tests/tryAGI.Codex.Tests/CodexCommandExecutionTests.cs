using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class CodexCommandExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static Task<JsonElement> Invoke(CodexCommandExecution host, string method, string json) =>
        host.HandleAsync(method, Json(json), Ct);
    private static Task<JsonElement> Exec(CodexCommandExecution host, string json) =>
        Invoke(host, "command/exec", json);

    [Fact]
    public async Task BufferedCommandPreservesExactArgumentsAndCapsEachStream()
    {
        var adapter = new Adapter();
        await using var host = new CodexCommandExecution("owner", adapter, (_, _, _) => throw new InvalidOperationException());
        var run = Exec(host, """{"command":["tool","a b",""],"outputBytesCap":3,"env":{"A":"v","B":null}}""");
        var process = await adapter.Started.Task.WaitAsync(Ct);
        Assert.Equal("owner", adapter.Request!.ConnectionId);
        Assert.Equal(["tool", "a b", ""], adapter.Request.Command);
        Assert.Equal("v", adapter.Request.Environment["A"]);
        Assert.Null(adapter.Request.Environment["B"]);
        await process.Emit(CodexCommandStream.Stdout, "abcde"u8.ToArray());
        await process.Emit(CodexCommandStream.Stderr, "12345"u8.ToArray());
        process.Exit(7);
        var response = await run;
        Assert.Equal(7, response.GetProperty("exitCode").GetInt32());
        Assert.Equal("abc", response.GetProperty("stdout").GetString());
        Assert.Equal("123", response.GetProperty("stderr").GetString());
        Assert.True(process.Cleaned);
    }

    [Fact]
    public async Task PtyStreamsBinaryOutputAndSupportsOwnedControls()
    {
        var adapter = new Adapter();
        var notifications = new List<JsonElement>();
        await using var host = new CodexCommandExecution("owner", adapter, (method, body, _) =>
        {
            Assert.Equal("command/exec/outputDelta", method);
            notifications.Add(body);
            return ValueTask.CompletedTask;
        });
        var run = Exec(host, """{"command":["tool"],"processId":"a","tty":true,"size":{"rows":25,"cols":80},"outputBytesCap":3}""");
        var process = await adapter.Started.Task.WaitAsync(Ct);
        Assert.True(adapter.Request!.StreamStdin);
        Assert.True(adapter.Request.StreamStdoutStderr);
        Assert.Equal(new CodexCommandTerminalSize(25, 80), adapter.Request.Size);
        await process.Emit(CodexCommandStream.Stdout, new byte[] { 0, 255 });
        await process.Emit(CodexCommandStream.Stdout, new byte[] { 3, 4 });
        await process.Emit(CodexCommandStream.Stdout, new byte[] { 5 });
        await Invoke(host, "command/exec/write", """{"processId":"a","deltaBase64":"AP8=","closeStdin":true}""");
        Assert.Equal(new byte[] { 0, 255 }, process.Input);
        Assert.True(process.StdinClosed);
        await Invoke(host, "command/exec/resize", """{"processId":"a","size":{"rows":40,"cols":100}}""");
        Assert.Equal(new CodexCommandTerminalSize(40, 100), process.Size);
        await Invoke(host, "command/exec/terminate", """{"processId":"a"}""");
        var response = await run;
        Assert.Equal(143, response.GetProperty("exitCode").GetInt32());
        Assert.Equal("", response.GetProperty("stdout").GetString());
        Assert.Equal("", response.GetProperty("stderr").GetString());
        Assert.Equal(2, notifications.Count);
        Assert.Equal("a", notifications[0].GetProperty("processId").GetString());
        Assert.Equal("stdout", notifications[0].GetProperty("stream").GetString());
        Assert.Equal("AP8=", notifications[0].GetProperty("deltaBase64").GetString());
        Assert.False(notifications[0].GetProperty("capReached").GetBoolean());
        Assert.Equal("Aw==", notifications[1].GetProperty("deltaBase64").GetString());
        Assert.True(notifications[1].GetProperty("capReached").GetBoolean());
        Assert.True(process.Cleaned);
    }

    [Fact]
    public async Task ConnectionsCannotControlEachOthersProcessesAndDuplicatesAreRejected()
    {
        var adapter = new Adapter();
        await using var owner = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        await using var foreign = new CodexCommandExecution("foreign", new Adapter(), (_, _, _) => ValueTask.CompletedTask);
        var run = Exec(owner, """{"command":["tool"],"processId":"same"}""");
        var process = await adapter.Started.Task.WaitAsync(Ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Exec(owner, """{"command":["tool"],"processId":"same"}"""));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            Invoke(foreign, "command/exec/terminate", """{"processId":"same"}"""));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() =>
            Invoke(owner, "command/exec/write", """{"processId":"same","deltaBase64":"YQ=="}"""));
        Assert.False(process.Completion.IsCompleted);
        await Invoke(owner, "command/exec/terminate", """{"processId":"same"}""");
        await run;
        Assert.True(process.Cleaned);
    }

    [Theory]
    [InlineData("""{"command":[]}""")]
    [InlineData("""{"command":["tool"],"streamStdoutStderr":true}""")]
    [InlineData("""{"command":["tool"],"cwd":"relative"}""")]
    [InlineData("""{"command":["tool"],"size":{"rows":1,"cols":1}}""")]
    [InlineData("""{"command":["tool"],"sandboxPolicy":{},"permissionProfile":"p"}""")]
    [InlineData("""{"command":["tool"],"disableTimeout":true}""")]
    [InlineData("""{"command":["tool"],"disableOutputCap":true}""")]
    [InlineData("""{"command":["tool"],"timeoutMs":-1}""")]
    [InlineData("""{"command":["tool"],"outputBytesCap":-1}""")]
    [InlineData("""{"command":["tool"],"env":{"A":"one","A":"two"}}""")]
    [InlineData("""{"command":["tool"],"unexpectedGrant":true}""")]
    public async Task InvalidOrUnboundedRequestsNeverReachAdapter(string json)
    {
        var adapter = new Adapter();
        await using var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Exec(host, json));
        Assert.False(adapter.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task AdapterDenialIsNotRetriedOrDowngraded()
    {
        var adapter = new Adapter { Deny = true };
        await using var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Exec(host, """{"command":["tool"],"permissionProfile":"unavailable"}"""));
        Assert.Equal(1, adapter.Attempts);
        Assert.False(adapter.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task DisconnectWaitsForCleanupAndStopsAdmission()
    {
        var adapter = new Adapter { HoldCleanup = true };
        var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        var run = Exec(host, """{"command":["tool"]}""");
        var process = await adapter.Started.Task.WaitAsync(Ct);
        var shutdown = host.DisposeAsync().AsTask();
        await process.CleanupStarted.Task.WaitAsync(Ct);
        Assert.False(shutdown.IsCompleted);
        Assert.False(run.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Exec(host, """{"command":["tool"]}"""));
        process.AllowCleanup.TrySetResult();
        await shutdown.WaitAsync(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(process.Cleaned);
    }

    [Fact]
    public async Task CancellationRetainsCapacityUntilCleanupCompletes()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var adapter = new Adapter { HoldCleanup = true };
        await using var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask, 1);
        var run = host.HandleAsync("command/exec", Json("""{"command":["tool"]}"""), cancellation.Token);
        var process = await adapter.Started.Task.WaitAsync(Ct);
        await cancellation.CancelAsync();
        await process.CleanupStarted.Task.WaitAsync(Ct);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Exec(host, """{"command":["second"]}"""));
        process.AllowCleanup.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(process.Cleaned);
    }

    [Fact]
    public async Task CleanupFailureIsNotReportedAsSuccessfulDisconnect()
    {
        var adapter = new Adapter { FailCleanup = true };
        var host = new CodexCommandExecution("owner", adapter, (_, _, _) => ValueTask.CompletedTask);
        var run = Exec(host, """{"command":["tool"]}""");
        var process = await adapter.Started.Task.WaitAsync(Ct);
        process.Exit(0);
        await Assert.ThrowsAsync<IOException>(() => run);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Exec(host, """{"command":["second"]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());
    }

    private sealed class Adapter : ICodexCommandAdapter
    {
        internal TaskCompletionSource<Session> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CodexCommandRequest? Request { get; private set; }
        internal bool Deny { get; init; }
        internal bool HoldCleanup { get; init; }
        internal bool FailCleanup { get; init; }
        internal int Attempts { get; private set; }

        public Task<ICodexCommandSession> StartAsync(CodexCommandRequest request,
            Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
            CancellationToken cancellationToken)
        {
            Attempts++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Deny) throw new UnauthorizedAccessException();
            Request = request;
            var session = new Session(output, HoldCleanup, FailCleanup);
            Started.TrySetResult(session);
            return Task.FromResult<ICodexCommandSession>(session);
        }
    }

    private sealed class Session(Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        bool holdCleanup, bool failCleanup) : ICodexCommandSession
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cleaned { get; private set; }
        internal byte[]? Input { get; private set; }
        internal bool StdinClosed { get; private set; }
        internal CodexCommandTerminalSize? Size { get; private set; }
        public Task<int> Completion => completion.Task;
        internal ValueTask Emit(CodexCommandStream stream, byte[] bytes) => output(stream, bytes, Ct);
        internal void Exit(int code) => completion.TrySetResult(code);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, bool closeStdin, CancellationToken cancellationToken)
        {
            Input = bytes.ToArray();
            StdinClosed = closeStdin;
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(CodexCommandTerminalSize size, CancellationToken cancellationToken)
        {
            Size = size;
            return ValueTask.CompletedTask;
        }
        public ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            Exit(143);
            return ValueTask.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            CleanupStarted.TrySetResult();
            if (holdCleanup) await AllowCleanup.Task.WaitAsync(Ct);
            if (failCleanup) throw new IOException("Synthetic cleanup failure.");
            Exit(143);
            Cleaned = true;
        }
    }
}
