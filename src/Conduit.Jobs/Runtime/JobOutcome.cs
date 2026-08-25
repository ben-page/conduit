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
    /// The host stopped, or another host took the claim. No outcome is recorded: on shutdown the
    /// claim is released so another host resumes immediately, and on a lost claim the row already
    /// belongs to someone else.
    /// </summary>
    ClaimLost,
}
