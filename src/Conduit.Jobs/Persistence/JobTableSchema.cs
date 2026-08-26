namespace Conduit.Jobs.Persistence;

/// <summary>
/// Module-owned table identifiers for the jobs subsystem. The store composes its DDL and CRUD SQL
/// from these constants plus the engine type vocabulary on <see cref="Conduit.Persistence.ISqlDialect"/>,
/// so table names stay owned by the jobs module while engine-specific syntax stays in the dialect
/// (mirroring durability's <c>OutboxTableSchema</c> and audit's <c>AuditTableSchema</c>).
/// </summary>
public static class JobTableSchema
{
    /// <summary>The per-schedule cursor and pause-state table.</summary>
    public const string SchedulesTable = "conduit_job_schedules";

    /// <summary>The work-in-flight run ledger. Holds in-flight and failed rows only, never history.</summary>
    public const string RunsTable = "conduit_job_runs";

    /// <summary>
    /// The coalescing buffer. One row per distinct coalesce key with work waiting; deleted when that
    /// work becomes a run.
    /// </summary>
    public const string ScopesTable = "conduit_job_scopes";

    /// <summary>
    /// The durable run-outcome record. One row per terminal run (Completed, Failed, or Cancelled),
    /// written at the moment the outcome is reached — independent of the live ledger row's later
    /// fate. A coalescing pickup superseding a terminal run, or the retention purge, deletes only the
    /// ledger row; the outcome already recorded here is untouched.
    /// </summary>
    public const string HistoryTable = "conduit_job_run_history";
}
