using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeFeedbackDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task V2ReceiptsKeepTheirWireIdentityAcrossLocalSteeringButNeverRefreshAdmission()
    {
        using var socket = new DirectRealtimeTestSocket();
        await using var connection = new CodexRealtimeConnection(socket, 8192, "v2");
        var admission = connection.CaptureFeedbackAdmission();
        var delivery = new CodexRealtimeFeedbackDelivery();
        Task<bool> Send(CodexRealtimeFeedbackKind kind, string text, string id, bool current,
            CodexRealtimeConnection.FeedbackAdmission? token = null)
            => delivery.SendAsync(connection, token ?? admission, kind, text, id, "commentary", null, current, Ct);

        // Completing a turn with no written output must not claim that a result was sent.
        Assert.False(await Send(CodexRealtimeFeedbackKind.Complete, "", "root", false));
        Assert.False(await Send(CodexRealtimeFeedbackKind.Progress, "superseded message", "root", false));
        Assert.False(socket.Outgoing.Reader.TryRead(out _));

        Assert.True(await Send(CodexRealtimeFeedbackKind.Progress, "observed progress", "root", true));
        // These exact durable steering receipts were queued before another local steer.
        Assert.True(await Send(CodexRealtimeFeedbackKind.SteerAck, "", "steer-1", false));
        Assert.True(await Send(CodexRealtimeFeedbackKind.SteerAck, "", "steer-2", false));
        Assert.True(await Send(CodexRealtimeFeedbackKind.Complete, "", "root", false));
        Assert.False(await Send(CodexRealtimeFeedbackKind.Complete, "", "root", false));

        var items = new List<JsonElement>();
        while (socket.Outgoing.Reader.TryRead(out var wire))
        {
            using var json = JsonDocument.Parse(wire);
            if (json.RootElement.TryGetProperty("item", out var item)) items.Add(item.Clone());
        }
        var receipts = items.Where(i => i.GetProperty("type").GetString() == "function_call_output").ToArray();
        Assert.Equal(["steer-1", "steer-2", "root"], receipts.Select(i => i.GetProperty("call_id").GetString()).ToArray());
        Assert.Equal(CodexRealtimeFeedbackWire.SteerAck, receipts[0].GetProperty("output").GetString());
        Assert.Equal("", receipts[2].GetProperty("output").GetString());

        connection.InvalidatePendingFeedback();
        Assert.False(await Send(CodexRealtimeFeedbackKind.SteerAck, "", "old-steer", false));
        Assert.False(await Send(CodexRealtimeFeedbackKind.Progress, "old output", "old-root", true));
        Assert.False(socket.Outgoing.Reader.TryRead(out _));
        // A new generation must have its own delivery state, not inherit a stale result.
        var next = new CodexRealtimeFeedbackDelivery();
        Assert.False(await next.SendAsync(connection, connection.CaptureFeedbackAdmission(),
            CodexRealtimeFeedbackKind.Complete, "", "new-root", null, null, true, Ct));
    }
}
