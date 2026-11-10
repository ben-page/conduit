using Conduit.Persistence;

namespace Conduit.Jobs.Persistence;

/// <summary>
/// Claim schema for <c>conduit_job_runs</c>. <see cref="CreatedAtColumn"/> maps to <c>RunAt</c>: the
/// claim's eligibility comparison against <c>@GraceCutoff</c> then reads as "this job is due", which
/// is what turns the outbox's "claim pending entries" SQL into "claim jobs whose run time has
/// arrived" with no new per-engine SQL. <see cref="NextAttemptAtColumn"/> maps to <c>RunAt</c> too: a
/// retry moves <c>RunAt</c> to its backoff time, so the run's due time is its next attempt, and with
/// both cutoffs bound to the current instant the two comparisons agree.
/// </summary>
public sealed class JobRunsTableSchema : IClaimableTableSchema
{
    private JobRunsTableSchema()
    {
    }

    /// <summary>The shared instance.</summary>
    public static JobRunsTableSchema Default { get; } = new();

    /// <inheritdoc/>
    public string TableName => JobTableSchema.RunsTable;

    /// <inheritdoc/>
    public string IdColumn => "Id";

    /// <inheritdoc/>
    public string MessageIdColumn => "JobKey";

    /// <inheritdoc/>
    public string StatusColumn => "Status";

    /// <inheritdoc/>
    public string CreatedAtColumn => "RunAt";

    /// <inheritdoc/>
    public string NextAttemptAtColumn => "RunAt";

    /// <inheritdoc/>
    public string ClaimedAtColumn => "ClaimedAt";

    /// <inheritdoc/>
    public string ClaimTokenColumn => "ClaimToken";

    /// <inheritdoc/>
    public string AttemptCountColumn => "AttemptCount";
}
