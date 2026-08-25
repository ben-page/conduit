namespace Conduit.Jobs;

/// <summary>The outcome of a cancellation request.</summary>
public enum CancellationOutcome
{
    /// <summary>
    /// The flag was set and the running host will observe it within one heartbeat interval. This
    /// does <b>not</b> mean the work stopped — cancellation is cooperative, and a handler that
    /// ignores its <see cref="CancellationToken"/> runs to completion. Whatever it had already
    /// finished stays done.
    /// </summary>
    Requested = 0,

    /// <summary>No run with that key exists.</summary>
    NotFound = 1,

    /// <summary>The run had already reached a terminal state.</summary>
    AlreadyComplete = 2,
}
