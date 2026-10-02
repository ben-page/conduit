namespace Conduit.Jobs.Persistence;

/// <summary>
/// One row of the run ledger: a single occurrence or triggered job, in flight or terminal — or, for a
/// non-coalescing trigger run, kept as completed so a redelivered event still collapses on its key.
/// </summary>
public sealed class JobRun
{
    /// <summary>Auto-increment primary key. Claims are ordered by it.</summary>
    public long Id { get; init; }

    /// <summary>
    /// The deterministic run key — <c>{schedule}@{occurrenceUtcIso}</c> or
    /// <c>{trigger}@{eventMessageId}</c>. Unique, which is what makes materialization idempotent
    /// when several replicas race and what makes a redelivered event trigger run once.
    /// </summary>
    public required string JobKey { get; init; }

    /// <summary>
    /// The declaring schedule or trigger name. Overlap detection and the cursor are keyed on it, and
    /// it is how a claiming host maps the row back to its declaration for <c>MaxRuntime</c>.
    /// </summary>
    public string? ScheduleName { get; init; }

    /// <summary>
    /// The occurrence this run is for, on schedule runs only. Kept separate from
    /// <see cref="RunAt"/> because a retry moves <see cref="RunAt"/> to the backoff time, and the
    /// cursor must still advance to the occurrence.
    /// </summary>
    public DateTimeOffset? Occurrence { get; init; }

    /// <summary>Full type name of the command to dispatch.</summary>
    public required string MessageType { get; init; }

    /// <summary>The serialized command.</summary>
    public required string Payload { get; init; }

    /// <summary>When the job becomes due. The claim's eligibility column.</summary>
    public DateTimeOffset RunAt { get; init; }

    /// <summary>The row's lifecycle status.</summary>
    public JobRunStatus Status { get; init; }

    /// <summary>When the current claim was taken or last renewed.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>The token of the host currently holding the claim.</summary>
    public string? ClaimToken { get; init; }

    /// <summary>How many durable attempts have already failed.</summary>
    public int AttemptCount { get; init; }

    /// <summary>The message from the most recent failure.</summary>
    public string? LastError { get; init; }

    /// <summary>Set by an operator; observed by the running host on its next heartbeat.</summary>
    public bool CancellationRequested { get; init; }

    /// <summary>When the current attempt began. Drives <c>MaxRuntime</c> enforcement.</summary>
    public DateTimeOffset? StartedAt { get; init; }
}
