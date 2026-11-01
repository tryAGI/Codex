using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class RealtimeModelIdentityTests
{
    private sealed record SessionPayload(string Model);
    private sealed record EventPayload(string Type, SessionPayload Session);

    private static void Observe(CodexRealtimeModelIdentity identity, string json)
    {
        using var document = JsonDocument.Parse(json);
        identity.Observe(document.RootElement);
    }

    [Fact]
    public void AcceptedSessionWithoutModelDoesNotConfirmRequestedAlias()
    {
        var identity = new CodexRealtimeModelIdentity();
        Observe(identity, """{"type":"session.started","session":{"id":"test"}}""");
        var result = identity.Read("gpt-live-1.1-codex");
        Assert.Equal("gpt-live-1.1-codex", result.RequestedModel);
        Assert.Null(result.ProviderReportedModel);
        Assert.Null(result.ConfirmedModel);
        Assert.Equal("provider_model_unreported", result.Verification);
        Assert.Null(new CodexRealtimeModelIdentity().Read("gpt-live-1.1-codex").SourceEvent);
    }

    [Theory]
    [InlineData("session.started")]
    [InlineData("session.created")]
    [InlineData("session.updated")]
    public void MatchingProviderAliasIsExplicitlyScoped(string eventType)
    {
        var identity = new CodexRealtimeModelIdentity();
        Observe(identity, JsonSerializer.Serialize(new EventPayload(eventType, new("gpt-live-1-codex")), JsonSerializerOptions.Web));
        var result = identity.Read("gpt-live-1-codex");
        Assert.Equal("gpt-live-1-codex", result.ConfirmedModel);
        Assert.Equal("provider_alias_confirmed", result.Verification);
        Assert.Equal(eventType, result.SourceEvent);
        Assert.Contains("snapshot_and_billing_unverified", result.Scope);
        var changedRequest = identity.Read("gpt-live-1.1-codex");
        Assert.Equal("gpt-live-1-codex", changedRequest.ProviderReportedModel);
        Assert.Null(changedRequest.ConfirmedModel);
        Assert.Equal("provider_model_mismatch", changedRequest.Verification);
    }

    [Fact]
    public void PartialUpdatePreservesEvidenceButNewSessionClearsIt()
    {
        var identity = new CodexRealtimeModelIdentity();
        Observe(identity, """{"type":"session.started","session":{"model":"gpt-live-1-codex"}}""");
        Observe(identity, """{"type":"session.updated","session":{"voice":"sol"}}""");
        Assert.NotNull(identity.Read("gpt-live-1-codex").ConfirmedModel);
        Observe(identity, """{"type":"session.started","session":{}}""");
        Assert.Null(identity.Read("gpt-live-1-codex").ConfirmedModel);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"unsafe/value\"")]
    public void ExplicitInvalidModelUpdateInvalidatesEarlierConfirmation(string model)
    {
        var identity = new CodexRealtimeModelIdentity();
        Observe(identity, """{"type":"session.started","session":{"model":"gpt-live-1-codex"}}""");
        Observe(identity, "{\"type\":\"session.updated\",\"session\":{\"model\":" + model + "}}");
        Assert.Null(identity.Read("gpt-live-1-codex").ConfirmedModel);
        Assert.Equal("provider_model_unreported", identity.Read("gpt-live-1-codex").Verification);
    }

    [Theory]
    [InlineData("session.updated", "hello world")]
    [InlineData("session.started", "model/token")]
    [InlineData("delegation.created", "gpt-live-1-codex")]
    [InlineData("input_transcript.added", "gpt-live-1-codex")]
    public void OtherEventsAndUnsafeValuesCannotSupplyIdentity(string eventType, string model)
    {
        var identity = new CodexRealtimeModelIdentity();
        Observe(identity, JsonSerializer.Serialize(new EventPayload(eventType, new(model)), JsonSerializerOptions.Web));
        Assert.Null(identity.Read("gpt-live-1-codex").ProviderReportedModel);
    }
}
