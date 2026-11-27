namespace tryAGI.Codex;

// One V2 wire-root delivery receipt, shared only across local steers in the same
// transport admission. Queued output is not a transport receipt.
internal sealed class CodexRealtimeFeedbackDelivery
{
    private int outputWritten;

    internal static bool IsReceipt(CodexRealtimeFeedbackKind kind)
        => kind is CodexRealtimeFeedbackKind.Complete or CodexRealtimeFeedbackKind.SteerAck;

    internal async Task<bool> SendAsync(CodexRealtimeConnection connection,
        CodexRealtimeConnection.FeedbackAdmission admission, CodexRealtimeFeedbackKind kind,
        string text, string? id, string? channel, string? prefix, bool streamCurrent, CancellationToken ct)
    {
        if (!streamCurrent && !IsReceipt(kind)) return false;
        if (kind == CodexRealtimeFeedbackKind.Complete && Volatile.Read(ref outputWritten) == 0) return false;
        // Never manufacture a new speech admission for an old acknowledgement.
        var written = await connection.SendReasoningFeedbackAsync(admission, kind, text, id, channel, prefix, ct).ConfigureAwait(false);
        if (written && kind is CodexRealtimeFeedbackKind.Progress or CodexRealtimeFeedbackKind.Final or CodexRealtimeFeedbackKind.Item)
            Volatile.Write(ref outputWritten, 1);
        return written;
    }
}
