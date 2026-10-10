namespace Conduit.Jobs.Persistence;

/// <summary>What <see cref="IJobStore.TryMaterializeWithoutOverlapAsync"/> did with a run.</summary>
public enum MaterializeOutcome
{
    /// <summary>This call wrote the run.</summary>
    Materialized,

    /// <summary>
    /// A row with this run's key already exists, so nothing was written. For a schedule's occurrence
    /// this is the expected outcome on every replica but one: they all materialize the same key.
    /// </summary>
    AlreadyMaterialized,

    /// <summary>
    /// The schedule has another run outstanding (pending, awaiting a retry, or claimed), so writing
    /// this one would start the overlap the schedule forbids. Nothing was written.
    /// </summary>
    Overlapping,

    /// <summary>
    /// The run is for an occurrence at or before the schedule's cursor, so writing it would run it
    /// again: some host has taken it to a terminal outcome (on success its row is deleted; a failed or
    /// cancelled row is kept, but the cursor moved past it), or a host starting up skipped past it as a
    /// misfire. Nothing was written. The usual cause is a replica whose timer fired late.
    /// </summary>
    AlreadyCompleted,
}
