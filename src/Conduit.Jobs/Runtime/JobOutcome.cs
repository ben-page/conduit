namespace Conduit.Jobs.Runtime;

/// <summary>How one attempt at a job ended.</summary>
internal enum JobOutcome
{
    /// <summary>The handler returned. The row is deleted and the cursor advances.</summary>
    Completed,

    /// <summary>The handler threw. The ledger decides whether there is another attempt.</summary>
    Failed,

    /// <summary>
    /// An operator cancelled the run, or it exceeded its <c>MaxRuntime</c>. Terminal and never
    /// retried, and the cursor still advances — otherwise cancelling would loop forever.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The host is draining. The claim is released with the attempt count untouched, so a rolling
    /// deploy hands the work over at once instead of stalling it until the claim expires.
    /// </summary>
    HandedOver,

    /// <summary>
    /// Another host has taken the claim. Nothing is written — the row belongs to someone else now,
    /// and writing to it would clobber their claim.
    /// </summary>
    ClaimLost,
}
