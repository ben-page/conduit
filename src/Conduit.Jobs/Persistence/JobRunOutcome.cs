namespace Conduit.Jobs.Persistence;

/// <summary>
/// The terminal outcome a run history row records. Distinct from <see cref="JobRunStatus"/>, which
/// describes the live ledger's claim state machine and has no "succeeded" value because a successful
/// run's row is deleted rather than transitioned. History rows exist precisely to keep that outcome,
/// so this enum has all three terminal shapes and no live ones.
/// </summary>
public enum JobRunOutcome
{
    /// <summary>The run succeeded.</summary>
    Completed = 0,

    /// <summary>Attempts were exhausted without success.</summary>
    Failed = 1,

    /// <summary>Cancelled by an operator or by exceeding <c>MaxRuntime</c>.</summary>
    Cancelled = 2,
}
