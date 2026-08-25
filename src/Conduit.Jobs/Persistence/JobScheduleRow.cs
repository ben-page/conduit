namespace Conduit.Jobs.Persistence;

/// <summary>
/// One row of the schedules table: the durable state a code-declared schedule accumulates. The
/// declaration itself lives in code, so only the cursor and the pause flag are stored.
/// </summary>
public sealed class JobScheduleRow
{
    /// <summary>The schedule's name, matching its declaration.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The most recent occurrence that has reached a terminal outcome. Because a successful run's row
    /// is deleted, this cursor — not the row — is what stops a completed occurrence being
    /// re-materialized and re-run.
    /// </summary>
    public DateTimeOffset? LastCompletedOccurrence { get; init; }

    /// <summary>Whether future occurrences are suppressed. Cluster-wide and survives a restart.</summary>
    public bool IsPaused { get; init; }

    /// <summary>When the schedule was paused.</summary>
    public DateTimeOffset? PausedAt { get; init; }
}
