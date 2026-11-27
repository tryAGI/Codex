using System.Text.Json;

namespace tryAGI.Codex;

public sealed partial record CodexThreadDocument
{
    /// <summary>Durable ordered user submissions, consumed only by an atomic turn admission.</summary>
    public CodexStoredQueuedSubmission[]? QueuedSubmissions { get; init; }
}

/// <summary>One queued input. The stable submission identity becomes the admitted turn identity,
/// so an ambiguous admission can be inspected without replay.</summary>
/// <param name="Id">Stable submission identity.</param>
/// <param name="ClientUserMessageId">Original client message identity.</param>
/// <param name="Input">Original validated App Server user input array.</param>
/// <param name="ResponsesApiClientMetadata">Optional client metadata, retained until dispatch.</param>
public sealed record CodexStoredQueuedSubmission(Guid Id, string ClientUserMessageId, JsonElement Input,
    JsonElement? ResponsesApiClientMetadata);
