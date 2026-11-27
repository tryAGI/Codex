namespace tryAGI.Codex;

/// <summary>One-action execution boundary over the existing approval journal. The caller
/// must resolve an immutable action and its exact effective policy independently of model
/// data, and bind the dispatch callback to that same action. The callback must enforce
/// current host policy and the expiry at its actual admission boundary. Consumed is a
/// dispatch admission, never a completion receipt or permission to repeat an effect.</summary>
public sealed class CodexApprovalDispatcher
{
    private readonly CodexApprovalStore approvals;
    private readonly Func<CodexApprovalAction, CancellationToken, ValueTask> authorizeDispatch;

    /// <summary>Creates a dispatcher with an explicit current host policy check in addition
    /// to the original authenticated reviewer decision. A denied policy never consumes a grant.</summary>
    public CodexApprovalDispatcher(CodexApprovalStore approvals,
        Func<CodexApprovalAction, CancellationToken, ValueTask> authorizeDispatch)
    {
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.authorizeDispatch = authorizeDispatch ?? throw new ArgumentNullException(nameof(authorizeDispatch));
    }

    /// <summary>Consumes the exact approval before calling the bound execution adapter once.
    /// Missing, stale, denied, mismatched, expired or already consumed approvals refuse
    /// dispatch. Any uncertain CAS result is propagated, not recovered as permission.
    /// If the callback fails or cancellation wins after consumption, the grant stays consumed.
    /// This method does not implement effect recovery or cancellation of already admitted work.</summary>
    public async Task<TResult> DispatchAsync<TResult>(Guid approvalId, CodexApprovalAction exactAction,
        Func<CodexApprovalRecord, CancellationToken, Task<TResult>> dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactAction);
        ArgumentNullException.ThrowIfNull(dispatch);
        cancellationToken.ThrowIfCancellationRequested();
        await authorizeDispatch(exactAction, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var consumed = await approvals.TryConsumeAsync(approvalId, exactAction, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("No unexpired exact-action approval is available.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!approvals.IsUnexpired(consumed))
            throw new UnauthorizedAccessException("Approval expired before dispatch.");
        // Do not retry, even if the callback throws or its outcome is uncertain.
        return await dispatch(consumed, cancellationToken).ConfigureAwait(false);
    }
}
