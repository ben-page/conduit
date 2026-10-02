namespace Conduit.Jobs.Persistence;

/// <summary>
/// Lifecycle status of a run row. The <see cref="Pending"/> and <see cref="Claiming"/> values match
/// the outbox's, because the shared claim SQL binds them as <c>@PendingStatus</c> and
/// <c>@ClaimingStatus</c>. A successful run usually has no status — its row is deleted — except a
/// non-coalescing trigger run, which is kept as <see cref="Completed"/>.
/// </summary>
public enum JobRunStatus
{
    /// <summary>Materialized and awaiting a claim, or released after a failed attempt.</summary>
    Pending = 0,

    /// <summary>
    /// Succeeded, on a non-coalescing trigger run only, and kept until <c>CompletedRetention</c>
    /// expires. Its run key is <c>{trigger}@{eventMessageId}</c>, so the kept row is what makes a
    /// redelivery that arrives <i>after</i> the job finished collapse on the unique key rather than
    /// run the job again. Deleting it on success, as a schedule run is, would hold that only for
    /// redeliveries that raced the first run.
    /// </summary>
    Completed = 1,

    /// <summary>Attempts are exhausted. Terminal, retained for inspection until retention expires.</summary>
    Failed = 2,

    /// <summary>Claimed by exactly one host and running. A live host renews the claim on its heartbeat.</summary>
    Claiming = 3,

    /// <summary>
    /// Cancelled by an operator or by exceeding <c>MaxRuntime</c>. Terminal and never retried —
    /// retrying a cancelled run would defeat the cancellation. Distinct from <see cref="Failed"/> so
    /// the retry path cannot pick it up and so a deliberate action is not misreported as a failure.
    /// </summary>
    Cancelled = 4,
}
