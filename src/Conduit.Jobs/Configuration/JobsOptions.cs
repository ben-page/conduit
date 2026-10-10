namespace Conduit.Jobs.Configuration;

/// <summary>Tuning for the jobs runtime.</summary>
public sealed class JobsOptions
{
    /// <summary>How often a running host renews its claim. Default 30 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How stale a claim must be before another host may take it. Default 2 minutes — four missed
    /// heartbeats. Must be a comfortable multiple of <see cref="HeartbeatInterval"/>, or a slow
    /// database produces spurious re-claims and double execution.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Durable retry attempts before a run is marked failed. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long failed runs are retained for inspection. Default 7 days.</summary>
    public TimeSpan FailedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a completed non-coalescing trigger run is kept in the ledger. Default 1 day. This is
    /// the redelivery window: a broker redelivering the triggering event within it collapses on the
    /// run key, one redelivering later runs the job again. Schedule runs are unaffected — their
    /// cursor already records completion — and so are coalescing runs, whose key must be freed.
    /// </summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether to auto-create the jobs tables (schedules, runs, scopes, and run history) on startup.
    /// Default: true.
    /// </summary>
    public bool AutoCreateTable { get; set; } = true;

    /// <summary>How long run history is retained. Default 30 days.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Run no jobs on this host: <see cref="IJobs"/> still resolves, to list, pause, resume, trigger
    /// and cancel over the shared ledger, but none of the runner is registered — no scheduler, no
    /// claim sweep, no retention cleanup, no event-trigger listener. For a process that shares the
    /// jobs tables with another process whose runner owns execution: with an engine registered, a
    /// second runner would claim any due run in <c>conduit_job_runs</c>, including one whose handler
    /// lives only in the other process. The sibling of <c>OutboxOptions.CaptureOnly</c> and
    /// <see cref="Conduit.Configuration.ConduitOptions.ProducerOnly"/>, and independent of the
    /// latter: a producer-only transport host may still run jobs. Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// With an engine, schedules may still be declared, so this host can list and trigger them; they
    /// must match the runner's declarations (name, command, <c>AllowOverlap</c>), because
    /// trigger-now builds its command and applies the overlap rule from the declaration on the host
    /// it is called on. Without an engine there is no shared ledger for another host to run from, so
    /// any declaration is rejected at <c>AddJobs</c>. Event triggers are rejected either way: they
    /// fire only through the listener this mode does not register. The tables are still created
    /// (unless <see cref="AutoCreateTable"/> is off) and declared schedules still reconciled, since
    /// reading needs the tables and pause needs the schedule's row.
    /// </remarks>
    public bool ManageOnly { get; set; }
}
