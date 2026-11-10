namespace Conduit.Jobs.Configuration;

/// <summary>Tuning for the jobs runtime.</summary>
public sealed class JobsOptions
{
    // The ceilings mirror Conduit.Durability's DurationLimits (LongestDuration, LongestRetention), which this
    // package cannot reference: one day for an interval, a wait or a timeout, 3650 days for a retention. A timer
    // or Task.Delay cannot hold much past 49.7 days, and a duration added to or subtracted from now overflows near
    // TimeSpan.MaxValue.
    private static readonly TimeSpan LongestDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan LongestRetention = TimeSpan.FromDays(3650);

    /// <summary>How often a running host renews its claim. Must be positive. Default 30 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How stale a claim must be before another host may take it. Default 2 minutes — four missed
    /// heartbeats. Must be more than twice <see cref="HeartbeatInterval"/>, or a slow database produces
    /// spurious re-claims and double execution, and at most one day.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Durable attempts before a run is marked failed, the first included. Must be positive. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// The wait before each further attempt of a failed run: after the <c>n</c>th failed attempt the run is due
    /// <c>RetryBackoff[min(n - 1, Count - 1)]</c> later, so the last entry repeats. Must not be empty, and every
    /// entry must be positive and at most one day; for no retry at all, set <see cref="MaxAttempts"/> to 1.
    /// Default 1 minute, then 2, then 4.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryBackoff { get; set; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)];

    /// <summary>
    /// How long failed runs are retained for inspection. Must not be negative, and at most 3650 days. Default 7
    /// days.
    /// </summary>
    public TimeSpan FailedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a completed non-coalescing trigger run is kept in the ledger. Must not be negative, and at most
    /// 3650 days. Default 1 day. This is the redelivery window: a broker redelivering the triggering event within
    /// it collapses on the run key, one redelivering later runs the job again. Schedule runs are unaffected —
    /// their cursor already records completion — and so are coalescing runs, whose key must be freed.
    /// </summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How often the retention cleanup purges terminal runs and history past their retention. Must be positive,
    /// and at most one day. Default 1 hour.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether to auto-create the jobs tables (schedules, runs, scopes, and run history) on startup.
    /// Default: true.
    /// </summary>
    public bool AutoCreateTable { get; set; } = true;

    /// <summary>How long run history is retained. Must not be negative, and at most 3650 days. Default 30 days.</summary>
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

    /// <summary>The wait before the next attempt of a run that has failed <paramref name="failedAttempts"/> times.</summary>
    /// <param name="failedAttempts">The failed attempts so far, this one included: 1 after the first.</param>
    /// <returns>The <see cref="RetryBackoff"/> entry for that attempt; the last one repeats.</returns>
    internal TimeSpan BackoffAfter(int failedAttempts)
        => this.RetryBackoff[Math.Clamp(failedAttempts - 1, 0, this.RetryBackoff.Count - 1)];

    /// <summary>
    /// Refuses options the runtime cannot work with: a <see cref="HeartbeatInterval"/> that is not positive (the
    /// heartbeat would renew in a loop, a database round trip per iteration); a <see cref="ClaimTimeout"/> not
    /// more than twice it, or above one day; a <see cref="MaxAttempts"/> that is not positive; a retention that is
    /// negative or above 3650 days; a <see cref="CleanupInterval"/> that is not positive or is above one day; and
    /// an empty <see cref="RetryBackoff"/>, or one with an entry that is not positive or is above one day.
    /// <c>AddJobs</c> calls it before registering anything.
    /// </summary>
    /// <exception cref="InvalidOperationException">An option is invalid; the message names it.</exception>
    internal void Validate()
    {
        if (this.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"JobsOptions.HeartbeatInterval is {this.HeartbeatInterval}; it must be positive. A running host " +
                "renews its claim once per interval, so a zero interval renews in a loop.");
        }

        if (this.ClaimTimeout <= this.HeartbeatInterval * 2)
        {
            throw new InvalidOperationException(
                $"JobsOptions.ClaimTimeout ({this.ClaimTimeout}) must be more than twice " +
                $"HeartbeatInterval ({this.HeartbeatInterval}). A claim that can expire within two " +
                "heartbeats will be taken from a host that is still running the job, executing it twice.");
        }

        // The heartbeat bounds each renewal by a timer set to the time left before the claim timeout, and a timer
        // cannot hold much past 49 days: above that, every renewal would fail before it began.
        if (this.ClaimTimeout > LongestDuration)
        {
            throw new InvalidOperationException(
                $"JobsOptions.ClaimTimeout ({this.ClaimTimeout}) must be at most one day " +
                $"({LongestDuration}). Another host recovers a crashed host's run only once its claim is " +
                "that old.");
        }

        if (this.MaxAttempts <= 0)
        {
            throw new InvalidOperationException(
                $"JobsOptions.MaxAttempts is {this.MaxAttempts}; it must be positive. The first attempt counts, " +
                "so 1 means no retry.");
        }

        // The cleanup subtracts each retention from now: a negative one puts the cutoff in the future and purges
        // rows as soon as they are written, and one near TimeSpan.MaxValue throws on every pass, so nothing is ever
        // purged.
        RequireRetention(nameof(this.FailedRetention), this.FailedRetention);
        RequireRetention(nameof(this.CompletedRetention), this.CompletedRetention);
        RequireRetention(nameof(this.HistoryRetention), this.HistoryRetention);

        // The cleanup waits it between passes: a zero interval purges in a loop, and Task.Delay throws past about
        // 49.7 days, outside the pass's error handling, which ends the cleanup service (and, by default, the host).
        if (this.CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"JobsOptions.CleanupInterval is {this.CleanupInterval}; it must be positive. The cleanup waits " +
                "it between passes, so a zero interval purges in a loop.");
        }

        if (this.CleanupInterval > LongestDuration)
        {
            throw new InvalidOperationException(
                $"JobsOptions.CleanupInterval is {this.CleanupInterval}; it can be at most {LongestDuration}.");
        }

        if (this.RetryBackoff is null || this.RetryBackoff.Count == 0)
        {
            throw new InvalidOperationException(
                "JobsOptions.RetryBackoff is empty; it must hold the wait before at least one further attempt.");
        }

        for (var index = 0; index < this.RetryBackoff.Count; index++)
        {
            if (this.RetryBackoff[index] <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"JobsOptions.RetryBackoff[{index}] is {this.RetryBackoff[index]}; every entry must be positive, " +
                    "or a failing run would retry at once, on every sweep.");
            }

            // A failed attempt adds its backoff to now, which overflows near TimeSpan.MaxValue: the retry would
            // never be recorded, the claim would go stale, and the run would re-execute every ClaimTimeout with its
            // attempt count never moving.
            if (this.RetryBackoff[index] > LongestDuration)
            {
                throw new InvalidOperationException(
                    $"JobsOptions.RetryBackoff[{index}] is {this.RetryBackoff[index]}; it can be at most " +
                    $"{LongestDuration}. For no retry at all, set MaxAttempts to 1.");
            }
        }
    }

    private static void RequireRetention(string option, TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"JobsOptions.{option} is {value}; it cannot be negative.");
        }

        if (value > LongestRetention)
        {
            throw new InvalidOperationException(
                $"JobsOptions.{option} is {value}; it can be at most {LongestRetention}.");
        }
    }
}
