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
}
