namespace tryAGI.Codex.Tests;

// Exact secure routing and call-location assertions from rust-v0.172.0.
// This is a URI/parser fixture, not a network or provider test.
public sealed class RustParityRoutingTests
{
    [Fact]
    public void LegacyNestedV1BaseAppendsRealtimePath()
    {
        Assert.Equal("wss://example.com/openai/v1/realtime?intent=quicksilver&model=snapshot",
            CodexRealtimeProtocol.RealtimeSocketUri(new("https://example.com/openai/v1"), "v1", "snapshot").AbsoluteUri);
    }

    [Fact]
    public void LegacyV1BaseAppendsRealtimePath()
    {
        Assert.Equal("wss://api.openai.com/v1/realtime?intent=quicksilver&model=snapshot",
            CodexRealtimeProtocol.RealtimeSocketUri(new("https://api.openai.com/v1"), "v1", "snapshot").AbsoluteUri);
    }

    [Fact]
    public void SecureWebSocketBaseDefaultsToRealtimePath()
    {
        Assert.Equal("wss://example.com/v1/realtime?intent=quicksilver&model=realtime-test-model",
            CodexRealtimeProtocol.RealtimeSocketUri(new("wss://example.com"), "v1", "realtime-test-model").AbsoluteUri);
    }

    [Fact]
    public void V2RootWithoutModelOmitsIntent()
    {
        Assert.Equal("wss://example.com/v1/realtime",
            CodexRealtimeProtocol.RealtimeSocketUri(new("https://example.com"), "v2", null).AbsoluteUri);
    }

    [Fact]
    public void LegacyRootWithoutModelKeepsIntent()
    {
        Assert.Equal("wss://example.com/v1/realtime?intent=quicksilver",
            CodexRealtimeProtocol.RealtimeSocketUri(new("https://example.com"), "v1", null).AbsoluteUri);
    }

    [Fact]
    public void FramelessRewritesExistingRealtimePathAndPreservesQuery()
    {
        Assert.Equal("wss://example.com/v1/live?foo=bar&model=snapshot",
            CodexRealtimeProtocol.RealtimeSocketUri(new("wss://example.com/v1/realtime?foo=bar"), "v3", "snapshot").AbsoluteUri);
    }

    [Fact]
    public void V2ExistingCallUsesCallIdAndNoModelOrIntent()
    {
        Assert.Equal("wss://api.openai.com/v1/realtime?call_id=rtc_test",
            CodexRealtimeProtocol.SidebandUri(new("https://api.openai.com/v1"), "rtc_test", "v2").AbsoluteUri);
    }

    [Fact]
    public void LocationAcceptsExactRustUuid()
    {
        Assert.Equal("019eb97d-8e9a-7ff3-94b0-ea019babd5d7",
            CodexRealtimeProtocol.CallId("/v1/realtime/calls/019eb97d-8e9a-7ff3-94b0-ea019babd5d7"));
    }

    [Fact]
    public void LocationWithoutCallIdIsRejected()
    {
        Assert.Throws<CodexProtocolException>(() => CodexRealtimeProtocol.CallId("/v1/realtime/calls"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../../admin")]
    public void UntrustedFramelessCallIdsCannotBecomePaths(string id)
    {
        // The SDK intentionally admits only the trusted call-ID domain.
        Assert.Throws<CodexProtocolException>(() =>
            CodexRealtimeProtocol.SidebandUri(new("https://example.com/v1/live"), id, "v3"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8011")]
    [InlineData("ws://example.com")]
    public void UnencryptedRealtimeEndpointsAreRejectedBeforeTransport(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => CodexRealtimeProtocol.RealtimeSocketUri(new(endpoint), "v1", null));
    }
}
