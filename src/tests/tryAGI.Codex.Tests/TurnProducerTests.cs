using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class TurnProducerTests
{
    private static CodexWorkspacePatchResult Applied(string? before, string? after, bool preview = false) =>
        new(preview, new("/bound-workspace", "patch", "plan", []),
            [new("a.txt", before is null ? "create" : after is null ? "delete" : "update", Hash(before), Hash(after),
                before is null ? null : Encoding.UTF8.GetByteCount(before), after is null ? null : Encoding.UTF8.GetByteCount(after), null)])
        { DiffFiles = [new("a.txt", before, after)] };
    private static string? Hash(string? value) => value is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void DiffUsesInitialCaptureAndLatestVerifiedContent()
    {
        var tracker = new CodexTurnDiff();
        Assert.Contains("+middle", tracker.Apply(Applied("original\n", "middle\n")));
        var final = tracker.Apply(Applied("middle\n", "final\n"));
        Assert.Contains("-original\n", final); Assert.Contains("+final\n", final); Assert.DoesNotContain("middle", final);
        Assert.Equal("", tracker.Apply(Applied("final\n", "original\n")));
    }

    [Fact]
    public void UnknownCaptureAndHashOrChainMismatchDoNotReplaceState()
    {
        var tracker = new CodexTurnDiff(); tracker.Apply(Applied("first\n", "second\n"));
        Assert.Throws<ArgumentException>(() => tracker.Apply(Applied("first\n", "third\n")));
        Assert.Throws<ArgumentException>(() => tracker.Apply(Applied("second\n", "third\n") with { DiffFiles = [new("a.txt", "forged", "third\n")] }));
        Assert.Throws<ArgumentException>(() => tracker.Apply(Applied("second\n", "third\n") with { DiffFiles = null }));
        var final = tracker.Apply(Applied("second\n", "third\n"));
        Assert.Contains("-first\n", final); Assert.Contains("+third\n", final);
    }

    [Fact]
    public async Task PreviewNeverEmitsDiffAndSubscriberFailureDoesNotRepeatCommittedChange()
    {
        var deliveries = 0;
        var events = new CodexTurnEvents("thread", "turn", (_, _) => { deliveries++; return Task.FromException(new IOException("offline")); });
        await Assert.ThrowsAsync<ArgumentException>(() => events.AppliedPatchAsync(Applied("old", "new", preview: true)));
        Assert.Equal(0, deliveries);
        await events.AppliedPatchAsync(Applied("old", "new"));
        Assert.Equal(1, deliveries); Assert.Equal(1, events.DeliveryFailures);
        await events.AppliedPatchAsync(Applied("new", "next"));
        Assert.Equal(2, deliveries); Assert.Equal(2, events.DeliveryFailures);
    }

    [Fact]
    public async Task ProviderMetadataPreservesOpaqueValueAndExactHostIdentity()
    {
        JsonElement? seen = null;
        var events = new CodexTurnEvents("bound-thread", "bound-turn", (method, value) =>
        { Assert.Equal("turn/moderationMetadata", method); seen = value; return Task.CompletedTask; });
        using var payload = JsonDocument.Parse("""{"metadata":{"openai_chatgpt_moderation_metadata":["opaque",null,3]}}""");
        await events.ProviderAsync(new("response.created", payload.RootElement, 0), TestContext.Current.CancellationToken);
        Assert.Null(seen);
        await events.ProviderAsync(new("response.metadata", payload.RootElement, 0), TestContext.Current.CancellationToken);
        var result = seen ?? throw new InvalidDataException();
        Assert.Equal("bound-thread", result.GetProperty("threadId").GetString());
        Assert.Equal("bound-turn", result.GetProperty("turnId").GetString());
        Assert.True(JsonElement.DeepEquals(payload.RootElement.GetProperty("metadata").GetProperty("openai_chatgpt_moderation_metadata"), result.GetProperty("metadata")));
    }
}
