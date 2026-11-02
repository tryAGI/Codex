using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Connection-local provider identity evidence. A reported alias never proves a hidden snapshot or billing entitlement.</summary>
public sealed class CodexRealtimeModelIdentity
{
    /// <summary>What is known about the session model.</summary>
    /// <param name="RequestedModel">Model the session asked for.</param>
    /// <param name="ProviderReportedModel">Model the provider reported, if any.</param>
    /// <param name="ConfirmedModel">The requested model, when the provider reported the same name.</param>
    /// <param name="Verification"><c>provider_alias_confirmed</c>, <c>provider_model_mismatch</c> or <c>provider_model_unreported</c>.</param>
    /// <param name="SourceEvent">Event the report came from.</param>
    /// <param name="Scope">What the verification covers.</param>
    public sealed record Snapshot(string RequestedModel, string? ProviderReportedModel, string? ConfirmedModel,
        string Verification, string? SourceEvent,
        string Scope = "current_sideband_connection; provider_alias_only; snapshot_and_billing_unverified");

    private sealed record Observation(string? Model, string Event);
    private Observation? observation;

    internal void Observe(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("session.started" or "session.created" or "session.updated")) return;
        var eventType = type.GetString() ?? "";
        string? model = null;
        var hasModel = payload.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object &&
            session.TryGetProperty("model", out _);
        if (hasModel && session.GetProperty("model") is var value && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 and <= 128 } name &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) model = name;
        // Updates often omit unchanged fields. A fresh session must never inherit old evidence.
        if (hasModel || eventType != "session.updated")
            Volatile.Write(ref observation, new(model, eventType));
    }

    /// <summary>Compares the provider's last report with the requested model.</summary>
    public Snapshot Read(string requestedModel)
    {
        var observed = Volatile.Read(ref observation);
        var matches = observed?.Model is { } model && string.Equals(model, requestedModel, StringComparison.Ordinal);
        return new(requestedModel, observed?.Model, matches ? observed?.Model : null,
            observed?.Model is null ? "provider_model_unreported" : matches ? "provider_alias_confirmed" : "provider_model_mismatch",
            observed?.Event);
    }
}
