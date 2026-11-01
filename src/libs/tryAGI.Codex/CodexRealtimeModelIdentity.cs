using System.Text.Json;

namespace tryAGI.Codex;

/// <summary>Connection-local provider identity evidence. A reported alias never proves a hidden snapshot or billing entitlement.</summary>
public sealed class CodexRealtimeModelIdentity
{
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

    public Snapshot Read(string requestedModel)
    {
        var observed = Volatile.Read(ref observation);
        var matches = observed?.Model is { } model && string.Equals(model, requestedModel, StringComparison.Ordinal);
        return new(requestedModel, observed?.Model, matches ? observed?.Model : null,
            observed?.Model is null ? "provider_model_unreported" : matches ? "provider_alias_confirmed" : "provider_model_mismatch",
            observed?.Event);
    }
}
