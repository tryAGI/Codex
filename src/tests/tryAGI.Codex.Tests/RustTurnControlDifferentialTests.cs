using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex.Tests;

[Trait("Category", "RustDifferential")]
public sealed class RustTurnInterruptionDifferentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Interrupt(string thread, string turn) => new()
    {
        ["threadId"] = thread, ["turnId"] = turn,
    };

    private static void AssertInterrupted(JsonElement result, string turnId)
    {
        var turn = result.GetProperty("turn");
        Assert.Equal(turnId, turn.GetProperty("id").GetString());
        Assert.Equal("interrupted", turn.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InterruptBlockedResponseFencesWrongAndRepeatedTurnIdsWithoutReplay()
    {
        await using var f = await RustThreadModelDifferentialFixture.CreateAsync(
            enableSyntheticTurns: true, mode: RustSyntheticTurnMode.Interrupt);
        var provider = f.SyntheticTurns ?? throw new InvalidOperationException("Missing synthetic provider.");
        var ids = await f.StartAsync();
        var nativeStarted = await f.CallNative("turn/start", RustTurnLifecycleDifferentialTests.Input(ids.Native, "Synthetic interruption."));
        var rustStarted = await f.CallRust("turn/start", RustTurnLifecycleDifferentialTests.Input(ids.Rust, "Synthetic interruption."));
        var nativeTurn = RustTurnLifecycleDifferentialTests.TurnId(nativeStarted);
        var rustTurn = RustTurnLifecycleDifferentialTests.TurnId(rustStarted);
        // Wait until both model requests are actually in flight, not just
        // until a turn has been admitted into the host's journal.
        await provider.NativeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await provider.RustBlocked.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var wrong = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.CallNative("turn/interrupt", Interrupt(ids.Native, wrong)));
        await Assert.ThrowsAsync<CodexAppServerException>(() => f.CallRust("turn/interrupt", Interrupt(ids.Rust, wrong)));

        Assert.Empty((await f.CallNative("turn/interrupt", Interrupt(ids.Native, nativeTurn))
            .WaitAsync(TimeSpan.FromSeconds(30), Ct)).EnumerateObject());
        Assert.Empty((await f.CallRust("turn/interrupt", Interrupt(ids.Rust, rustTurn))).EnumerateObject());
        var rustTerminal = await f.WaitRustNotificationAsync("turn/completed", ids.Rust);
        var nativeTerminal = Assert.Single(f.NativeNotifications, e =>
            RustTurnLifecycleDifferentialTests.IsTerminal(e.Method, e.Payload, ids.Native, nativeTurn)).Payload;
        AssertInterrupted(nativeTerminal, nativeTurn);
        AssertInterrupted(rustTerminal, rustTurn);
        await provider.NativeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        provider.ReleaseRust.TrySetResult();

        await Assert.ThrowsAsync<CodexThreadRpcException>(() => f.CallNative("turn/interrupt", Interrupt(ids.Native, nativeTurn)));
        await Assert.ThrowsAsync<CodexAppServerException>(() => f.CallRust("turn/interrupt", Interrupt(ids.Rust, rustTurn)));
        await f.RestartAsync();
        foreach (var (call, thread, turn) in new (Func<string, JsonObject, Task<JsonElement>>, string, string)[]
        {
            (f.CallNative, ids.Native, nativeTurn), (f.CallRust, ids.Rust, rustTurn),
        })
        {
            await call("thread/resume", new JsonObject { ["threadId"] = thread, ["excludeTurns"] = true });
            var read = await call("thread/read", new JsonObject { ["threadId"] = thread, ["includeTurns"] = true });
            var stored = Assert.Single(read.GetProperty("thread").GetProperty("turns").EnumerateArray());
            Assert.Equal(turn, stored.GetProperty("id").GetString());
            Assert.Equal("interrupted", stored.GetProperty("status").GetString());
            Assert.DoesNotContain(stored.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("type").GetString() == "agentMessage");
        }
        Assert.Single(provider.NativeRequests);
        Assert.Single(provider.RustRequests);
        Assert.Empty(provider.Rejections);
    }
}

[Trait("Category", "RustDifferential")]
public sealed class RustToolContinuationDifferentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ToolResult(JsonElement request)
    {
        var output = Assert.Single(request.GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.Equal(RustSyntheticTurnProvider.ToolCallId, output.GetProperty("call_id").GetString());
        var payload = output.GetProperty("output");
        return payload.ValueKind == JsonValueKind.String ? payload.GetString()! :
            string.Concat(payload.EnumerateArray().Select(item => item.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task CorrelatedDynamicTextResultContinuesExactlyOnceOnBothRuntimes()
    {
        await using var f = await RustThreadModelDifferentialFixture.CreateAsync(
            enableSyntheticTurns: true, mode: RustSyntheticTurnMode.Tool);
        var provider = f.SyntheticTurns ?? throw new InvalidOperationException("Missing synthetic provider.");
        var ids = await f.StartAsync();
        // Rust exposes the dynamic tool via a server-request round trip; the
        // managed host supplies its authorized tool callback directly.
        await RustTurnLifecycleDifferentialTests.RunPairAsync(f, ids,
            "Synthetic tool continuation.", RustSyntheticTurnProvider.SecondAnswer);

        var nativeCall = Assert.Single(provider.NativeToolCalls);
        var rustCall = Assert.Single(provider.RustToolCalls);
        Assert.Equal(RustSyntheticTurnProvider.ToolCallId, nativeCall.Id);
        Assert.Equal(nativeCall.Id, rustCall.GetProperty("callId").GetString());
        Assert.Equal(nativeCall.Name, rustCall.GetProperty("tool").GetString());
        Assert.True(JsonElement.DeepEquals(RustThreadModelDifferentialFixture.Json(nativeCall.Input),
            rustCall.GetProperty("arguments")));
        Assert.Equal(ids.Rust, rustCall.GetProperty("threadId").GetString());

        var rustRead = await f.CallRust("thread/read", new JsonObject { ["threadId"] = ids.Rust, ["includeTurns"] = true });
        var nativeRead = await f.CallNative("thread/read", new JsonObject { ["threadId"] = ids.Native, ["includeTurns"] = true });
        var rustTurn = Assert.Single(rustRead.GetProperty("thread").GetProperty("turns").EnumerateArray());
        var nativeTurn = Assert.Single(nativeRead.GetProperty("thread").GetProperty("turns").EnumerateArray());
        Assert.Equal(rustTurn.GetProperty("id").GetString(), rustCall.GetProperty("turnId").GetString());
        Assert.Equal("completed", rustTurn.GetProperty("status").GetString());
        Assert.Equal("completed", nativeTurn.GetProperty("status").GetString());
        foreach (var requests in new[] { provider.NativeRequests.ToArray(), provider.RustRequests.ToArray() })
        {
            Assert.Equal(2, requests.Length);
            Assert.DoesNotContain(requests[0].GetProperty("input").EnumerateArray(), item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            Assert.Equal(RustSyntheticTurnProvider.ToolResultText, ToolResult(requests[1]));
        }
        Assert.Empty(provider.Rejections);
    }
}
