using System.Text.Json;
using System.Threading.Channels;

namespace tryAGI.Codex.Tests;

public sealed class CodexProcessExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static string SpawnJson(string extra = "") =>
        $$"""{"command":["tool","a b",""],"processHandle":"handle","cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}}{{extra}}}""";
    private static ValueTask Ack(JsonElement result, CancellationToken _)
    {
        Assert.Empty(result.EnumerateObject());
        return ValueTask.CompletedTask;
    }

    private static Task Spawn(CodexProcessExecution host, string extra = "", CancellationToken? ct = null,
        Func<JsonElement, CancellationToken, ValueTask>? respond = null) =>
        host.HandleAsync("process/spawn", Json(SpawnJson(extra)), respond ?? Ack, ct ?? Ct);
    private static Task Control(CodexProcessExecution host, string method, string json) =>
        host.HandleAsync(method, Json(json), Ack, Ct);

    [Fact]
    public async Task SpawnAcknowledgesBeforeExitAndReportsBothBufferedCapsAndDrain()
    {
        var adapter = new Adapter();
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        await using var host = Host(adapter, notices);
        await Spawn(host, ""","outputBytesCap":3""");
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        Assert.False(process.Completion.IsCompleted);
        Assert.Equal("handle", adapter.Request!.ProcessId);
        Assert.Equal(["tool", "a b", ""], adapter.Request.Command);
        Assert.NotEqual(Guid.Empty, adapter.Request.ExecutionId);
        await process.Emit(CodexCommandStream.Stdout, "abcde"u8.ToArray());
        await process.Emit(CodexCommandStream.Stderr, "12"u8.ToArray());
        Assert.False(notices.Reader.TryRead(out _));
        process.Exit(9, true);
        var notice = await Next(notices);
        Assert.Equal("process/exited", notice.Method);
        Assert.Equal("handle", notice.Body.GetProperty("processHandle").GetString());
        Assert.Equal(9, notice.Body.GetProperty("exitCode").GetInt32());
        Assert.True(notice.Body.GetProperty("outputDrainTimedOut").GetBoolean());
        Assert.Equal("abc", notice.Body.GetProperty("stdout").GetString());
        Assert.True(notice.Body.GetProperty("stdoutCapReached").GetBoolean());
        Assert.Equal("12", notice.Body.GetProperty("stderr").GetString());
        Assert.False(notice.Body.GetProperty("stderrCapReached").GetBoolean());
        Assert.True(process.Cleaned);
    }

    [Fact]
    public async Task EarlyPtyOutputAndInstantExitCannotOvertakeSpawnResponse()
    {
        var adapter = new Adapter { EarlyOutput = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        var ackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = Host(adapter, notices);
        var spawning = Spawn(host, ""","tty":true,"size":{"rows":20,"cols":80},"outputBytesCap":2""",
            respond: async (body, ct) =>
            {
                await Ack(body, ct);
                ackStarted.TrySetResult();
                await ackAllowed.Task.WaitAsync(ct);
            });
        await ackStarted.Task.WaitAsync(Ct);
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        Assert.True(adapter.Request!.StreamStdin);
        Assert.True(adapter.Request.StreamStdoutStderr);
        Assert.Equal(new CodexCommandTerminalSize(20, 80), adapter.Request.Size);
        process.Exit(0, false);
        Assert.False(notices.Reader.TryRead(out _));
        ackAllowed.TrySetResult();
        await spawning;
        var delta = await Next(notices);
        Assert.Equal("process/outputDelta", delta.Method);
        Assert.Equal("AP8=", delta.Body.GetProperty("deltaBase64").GetString());
        Assert.Equal("stdout", delta.Body.GetProperty("stream").GetString());
        Assert.True(delta.Body.GetProperty("capReached").GetBoolean());
        var exit = await Next(notices);
        Assert.Equal("process/exited", exit.Method);
        Assert.Equal("", exit.Body.GetProperty("stdout").GetString());
        Assert.True(exit.Body.GetProperty("stdoutCapReached").GetBoolean());
        Assert.False(exit.Body.GetProperty("outputDrainTimedOut").GetBoolean());
    }

    [Fact]
    public async Task ProcessControlsAreOwnedAndRequestCancellationDoesNotKillAcknowledgedProcess()
    {
        var adapter = new Adapter();
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        await using var host = Host(adapter, notices);
        await using var foreign = Host(new Adapter(), Channel.CreateUnbounded<(string, JsonElement)>());
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await Spawn(host, ""","tty":true""", request.Token);
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        await request.CancelAsync();
        Assert.False(adapter.Token.IsCancellationRequested);
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Spawn(host));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Control(foreign, "process/kill", """{"processHandle":"handle"}"""));
        await Control(host, "process/writeStdin", """{"processHandle":"handle","deltaBase64":"AP8=","closeStdin":true}""");
        Assert.Equal(new byte[] { 0, 255 }, process.Input);
        Assert.True(process.StdinClosed);
        await Control(host, "process/resizePty", """{"processHandle":"handle","size":{"rows":40,"cols":100}}""");
        Assert.Equal(new CodexCommandTerminalSize(40, 100), process.Size);
        await Control(host, "process/kill", """{"processHandle":"handle"}""");
        var exit = await Next(notices);
        Assert.Equal(143, exit.Body.GetProperty("exitCode").GetInt32());
        Assert.True(process.Cleaned);
    }

    [Fact]
    public async Task FailedStartReleasesHandleAndDoesNotAcknowledge()
    {
        var adapter = new Adapter { Deny = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        await using var host = Host(adapter, notices);
        var acks = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Spawn(host, respond: (_, _) =>
        {
            acks++;
            return ValueTask.CompletedTask;
        }));
        Assert.Equal(0, acks);
        Assert.False(notices.Reader.TryRead(out _));
        adapter.Deny = false;
        await Spawn(host);
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        process.Exit(0);
        Assert.Equal("process/exited", (await Next(notices)).Method);
        Assert.Equal(2, adapter.Attempts);
    }

    [Fact]
    public async Task DisconnectWaitsForCleanupAndSuppressesTerminalNotification()
    {
        var adapter = new Adapter { HoldCleanup = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        var host = Host(adapter, notices);
        await Spawn(host);
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        var close = host.DisposeAsync().AsTask();
        await process.CleanupStarted.Task.WaitAsync(Ct);
        Assert.False(close.IsCompleted);
        Assert.True(adapter.Token.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Spawn(host));
        process.AllowCleanup.TrySetResult();
        await close.WaitAsync(Ct);
        Assert.True(process.Cleaned);
        Assert.False(notices.Reader.TryRead(out _));
    }

    [Fact]
    public async Task FailingCancellationCallbackDoesNotSkipCleanup()
    {
        var adapter = new Adapter { HoldCleanup = true, ThrowCancellation = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        var host = Host(adapter, notices);
        await Spawn(host);
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        var close = host.DisposeAsync().AsTask();
        await process.CleanupStarted.Task.WaitAsync(Ct);
        Assert.False(close.IsCompleted);
        process.AllowCleanup.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => close);
        Assert.True(process.Cleaned);
        Assert.False(notices.Reader.TryRead(out _));
    }

    [Fact]
    public async Task FailedAcknowledgementCleansUpWithoutOutput()
    {
        var adapter = new Adapter { EarlyOutput = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        await using var host = Host(adapter, notices);
        await Assert.ThrowsAsync<IOException>(() => Spawn(host, ""","streamStdoutStderr":true""",
            respond: (_, _) => throw new IOException("Transport failed.")));
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        Assert.True(process.Cleaned);
        Assert.False(notices.Reader.TryRead(out _));
    }

    [Fact]
    public async Task UnknownExitAndCleanupFailureNeverProduceSuccessfulExit()
    {
        var adapter = new Adapter { FailCleanup = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        var failures = Channel.CreateUnbounded<CodexProcessFailure>();
        var host = Host(adapter, notices, failures);
        await Spawn(host);
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        process.Exit(0);
        var failure = await failures.Reader.ReadAsync(Ct);
        Assert.Equal(adapter.Request!.ExecutionId, failure.ExecutionId);
        Assert.IsType<IOException>(failure.Error);
        Assert.False(notices.Reader.TryRead(out _));
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Spawn(host));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task AmbiguousOutputDeliveryIsNotRetried()
    {
        var adapter = new Adapter { EarlyOutput = true };
        var failures = Channel.CreateUnbounded<CodexProcessFailure>();
        var calls = 0;
        await using var host = new CodexProcessExecution("owner", adapter, (method, _, _) =>
        {
            Assert.Equal("process/outputDelta", method);
            Interlocked.Increment(ref calls);
            throw new IOException("Delivery may have succeeded.");
        }, (failure, _) => { failures.Writer.TryWrite(failure); return ValueTask.CompletedTask; });
        await Spawn(host, ""","streamStdoutStderr":true""");
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        Assert.IsType<IOException>((await failures.Reader.ReadAsync(Ct)).Error);
        Assert.Equal(1, calls);
        Assert.True(process.Cleaned);
    }

    [Theory]
    [InlineData(""","timeoutMs":null""")]
    [InlineData(""","outputBytesCap":null""")]
    [InlineData(""","sandboxPolicy":{}""")]
    [InlineData(",\"permissionProfile\":\"host\"")]
    [InlineData(""","size":{"rows":20,"cols":80}""")]
    [InlineData(",\"processHandle\":\"second\"")]
    public async Task UnsupportedOrInvalidRequestsNeverStart(string extra)
    {
        var adapter = new Adapter();
        await using var host = Host(adapter, Channel.CreateUnbounded<(string, JsonElement)>());
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => Spawn(host, extra));
        Assert.Equal(0, adapter.Attempts);
    }

    [Fact]
    public async Task TimeoutReportsUpstreamExitCodeOnlyAfterCleanup()
    {
        var adapter = new Adapter { HoldCleanup = true };
        var notices = Channel.CreateUnbounded<(string Method, JsonElement Body)>();
        await using var host = Host(adapter, notices);
        await Spawn(host, ""","timeoutMs":100""");
        var process = await adapter.Started.Reader.ReadAsync(Ct);
        await process.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(notices.Reader.TryRead(out _));
        process.AllowCleanup.TrySetResult();
        var exit = await Next(notices);
        Assert.Equal(124, exit.Body.GetProperty("exitCode").GetInt32());
        Assert.False(exit.Body.TryGetProperty("outputDrainTimedOut", out _));
        Assert.True(process.Cleaned);
    }

    private static Task<(string Method, JsonElement Body)> Next(Channel<(string Method, JsonElement Body)> notices) =>
        notices.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);

    private static CodexProcessExecution Host(Adapter adapter, Channel<(string Method, JsonElement Body)> notices,
        Channel<CodexProcessFailure>? failures = null) =>
        new("owner", adapter, (method, body, _) => { notices.Writer.TryWrite((method, body)); return ValueTask.CompletedTask; },
            (failure, _) =>
            {
                if (failures is null) throw new InvalidOperationException("Unexpected process failure.", failure.Error);
                failures.Writer.TryWrite(failure);
                return ValueTask.CompletedTask;
            });

    private sealed class Adapter : ICodexProcessAdapter
    {
        internal Channel<Session> Started { get; } = Channel.CreateUnbounded<Session>();
        internal CodexCommandRequest? Request { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal bool Deny { get; set; }
        internal bool EarlyOutput { get; init; }
        internal bool HoldCleanup { get; init; }
        internal bool FailCleanup { get; init; }
        internal bool ThrowCancellation { get; init; }
        internal int Attempts { get; private set; }

        public async Task<ICodexProcessSession> StartAsync(CodexCommandRequest request,
            Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
            CancellationToken cancellationToken)
        {
            Attempts++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Deny) throw new UnauthorizedAccessException();
            Request = request;
            Token = cancellationToken;
            if (ThrowCancellation) _ = cancellationToken.Register(() => throw new IOException("Synthetic cancellation callback failure."));
            var session = new Session(output, HoldCleanup, FailCleanup);
            if (EarlyOutput) await session.Emit(CodexCommandStream.Stdout, new byte[] { 0, 255, 42 });
            Started.Writer.TryWrite(session);
            return session;
        }
    }

    private sealed class Session(Func<CodexCommandStream, ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        bool holdCleanup, bool failCleanup) : ICodexProcessSession
    {
        private readonly TaskCompletionSource<CodexProcessExit> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cleaned { get; private set; }
        internal byte[]? Input { get; private set; }
        internal bool StdinClosed { get; private set; }
        internal CodexCommandTerminalSize? Size { get; private set; }
        public Task<CodexProcessExit> Completion => completion.Task;
        internal ValueTask Emit(CodexCommandStream stream, byte[] bytes) => output(stream, bytes, Ct);
        internal void Exit(int code, bool? drain = null) => completion.TrySetResult(new(code, drain));

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
