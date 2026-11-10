using System.Collections.Concurrent;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Runs a claimed job to an outcome: heartbeat alongside, dispatch through the runner, then record
/// what happened. Shared by the scheduler and by event triggers, which reach the same state from
/// different directions — the scheduler claims a due row, a trigger writes one already claimed —
/// but from there owe the ledger exactly the same bookkeeping.
/// </summary>
internal sealed partial class ClaimedJobExecutor
{
    private readonly ScheduleRegistry registry;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly JobRunner runner;
    private readonly TimeProvider timeProvider;
    private readonly IOptions<JobsOptions> options;
    private readonly IHostApplicationLifetime lifetime;
    private readonly JobSweepSignal sweepSignal;
    private readonly ILogger<ClaimedJobExecutor> logger;
    private readonly ConcurrentDictionary<long, (string JobKey, Task Task)> inFlight = new();

    public ClaimedJobExecutor(
        ScheduleRegistry registry,
        IServiceScopeFactory scopeFactory,
        JobRunner runner,
        TimeProvider timeProvider,
        IOptions<JobsOptions> options,
        IHostApplicationLifetime lifetime,
        JobSweepSignal sweepSignal,
        ILogger<ClaimedJobExecutor> logger)
    {
        this.registry = registry;
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.timeProvider = timeProvider;
        this.options = options;
        this.lifetime = lifetime;
        this.sweepSignal = sweepSignal;
        this.logger = logger;
    }

    /// <summary>The keys of the runs this host is executing now, for the log line of a drain that ran out of time.</summary>
    public IReadOnlyList<string> RunningJobKeys => [.. this.inFlight.Values.Select(running => running.JobKey)];

    /// <summary>
    /// Begins running a claimed job without waiting for it. Neither caller may block on the job: the
    /// scheduler would delay the next occurrence and the next sweep, and an event trigger would hold
    /// the publishing dispatch open for the job's entire duration.
    /// </summary>
    public void Start(JobRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var task = Task.Run(() => this.RunAsync(run), CancellationToken.None);
        this.inFlight[run.Id] = (run.JobKey, task);
        _ = task.ContinueWith(
            _ => this.inFlight.TryRemove(run.Id, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for every in-flight run to finish. A draining host must not exit while still holding
    /// claims — that is the stall releasing them exists to avoid. Unbounded: the scheduler bounds it by the
    /// host's shutdown deadline.
    /// </summary>
    public Task DrainAsync() => Task.WhenAll(this.inFlight.Values.Select(running => running.Task));

    private async Task RunAsync(JobRun run)
    {
        try
        {
            await this.ExecuteAsync(run);
        }
        finally
        {
            // Whatever the outcome, it may let the sweep take work now: a completion frees a coalescing key and
            // the overlap lock, a retry may be due sooner than the sweep meant to wake.
            this.sweepSignal.Wake();
        }
    }

    private async Task ExecuteAsync(JobRun run)
    {
        if (run.CancellationRequested)
        {
            // An operator cancelled it while it waited — pending, or awaiting a retry — and only the claim's
            // read-back carries the flag, since no heartbeat has run yet. Dispatching it anyway would run a short
            // job to Completed against the request. Terminal like any cancellation, under the claim, with history.
            await this.RecordOutcomeAsync(run, JobOutcome.Cancelled, error: null);
            return;
        }

        var stoppingToken = this.lifetime.ApplicationStopping;
        if (stoppingToken.IsCancellationRequested)
        {
            // Claimed after the host began stopping — an event trigger's run, from a delivery a broker drains after
            // ApplicationStopping. Dispatching it would start the handler under a token already cancelled, and a
            // handler that ignores its token would run in full on a host on its way out. Hand it back untouched, so
            // another host runs it whole.
            await this.RecordOutcomeAsync(run, JobOutcome.HandedOver, error: null);
            return;
        }

        // Linked to the host's stopping token, so a graceful drain reaches the handler the same way
        // an operator's cancellation and a MaxRuntime breach do — the runner tells them apart after.
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        JobAttempt attempt;
        var heartbeat = ClaimHeartbeat.Start(
            run,
            this.registry.MaxRuntimeFor(run.ScheduleName),
            this.scopeFactory,
            this.timeProvider,
            this.options,
            jobCancellation,
            this.logger);

        await using (heartbeat.ConfigureAwait(false))
        {
            attempt = await this.runner.ExecuteAsync(
                run, () => heartbeat.CancellationRequested || heartbeat.ClaimLost,
                stoppingToken, jobCancellation.Token);
        }

        // A lost claim outranks everything the runner concluded: the row belongs to another host now,
        // so this one must write nothing at all to it.
        var outcome = heartbeat.ClaimLost ? JobOutcome.ClaimLost : attempt.Outcome;
        await this.RecordOutcomeAsync(run, outcome, attempt.Error);
    }

    private async Task RecordOutcomeAsync(JobRun run, JobOutcome outcome, Exception? error)
    {
        if (outcome == JobOutcome.ClaimLost)
        {
            LogClaimLost(this.logger, run.JobKey);
            return;
        }

        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

            // Every outcome write is conditional on this host's claim. When another host has taken the
            // run over since the heartbeat last renewed, the write changes nothing and the outcome is
            // that host's to record.
            var recorded = true;
            switch (outcome)
            {
                case JobOutcome.Completed:
                    recorded = await store.CompleteAsync(
                        run, run.ClaimToken!, this.CursorScheduleFor(run), run.Occurrence,
                        retainRunKey: this.registry.IsMessageKeyedTrigger(run.ScheduleName),
                        this.timeProvider.GetUtcNow(), CancellationToken.None);
                    break;

                case JobOutcome.Cancelled:
                    // Terminal and never retried — retrying would defeat the cancellation. The cursor
                    // still advances, or the occurrence would be re-materialized and cancelling would
                    // loop forever.
                    recorded = await store.MarkTerminalAsync(
                        run, run.ClaimToken!, JobRunStatus.Cancelled, null,
                        this.CursorScheduleFor(run), run.Occurrence, this.timeProvider.GetUtcNow(),
                        CancellationToken.None);
                    break;

                case JobOutcome.HandedOver:
                    // A handover, not an attempt: the count is untouched so the next host starts
                    // where this one left off rather than one attempt closer to giving up.
                    await store.ReleaseAsync(run.Id, run.ClaimToken!, CancellationToken.None);
                    break;

                case JobOutcome.Failed:
                    recorded = await this.RecordFailureAsync(store, run, error);
                    break;

                case JobOutcome.ClaimLost:
                default:
                    await store.ReleaseAsync(run.Id, run.ClaimToken!, CancellationToken.None);
                    break;
            }

            if (!recorded)
            {
                LogClaimLost(this.logger, run.JobKey);
            }
        }
#pragma warning disable CA1031 // Losing the outcome write must not tear the host down; the claim goes
        catch (Exception exception) // stale and another host recovers the run.
#pragma warning restore CA1031
        {
            LogOutcomeNotRecorded(this.logger, run.JobKey, exception);
        }
    }

    /// <summary>
    /// Decides whether this failure gets another attempt. Conduit.Resilience has already had its say
    /// about <i>this</i> attempt; the ledger decides whether there is <i>another</i> one, and only the
    /// ledger survives a restart.
    /// </summary>
    /// <returns>Whether the failure was recorded, which it is only while this host holds the claim.</returns>
    private async Task<bool> RecordFailureAsync(IJobStore store, JobRun run, Exception? error)
    {
        var message = error?.Message ?? "The job failed without an exception message.";
        var attempts = run.AttemptCount + 1;

        if (attempts >= this.options.Value.MaxAttempts)
        {
            // The cursor advances even though the occurrence never succeeded: one permanently broken
            // occurrence must not block every later one, and the failed row keeps it visible.
            if (!await store.MarkTerminalAsync(
                run, run.ClaimToken!, JobRunStatus.Failed, message,
                this.CursorScheduleFor(run), run.Occurrence, this.timeProvider.GetUtcNow(),
                CancellationToken.None))
            {
                return false;
            }

            LogAttemptsExhausted(this.logger, run.JobKey, attempts);
            return true;
        }

        var nextRunAt = this.timeProvider.GetUtcNow() + this.options.Value.BackoffAfter(attempts);
        if (!await store.IncrementAttemptAsync(run.Id, run.ClaimToken!, nextRunAt, message, CancellationToken.None))
        {
            return false;
        }

        LogRetryScheduled(this.logger, run.JobKey, attempts, nextRunAt);
        return true;
    }

    /// <summary>
    /// The schedule whose cursor this run should advance, or <see langword="null"/> when the run came
    /// from an event trigger and there is no cursor to move.
    /// </summary>
    private string? CursorScheduleFor(JobRun run)
        => this.registry.IsSchedule(run.ScheduleName) ? run.ScheduleName : null;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job run '{JobKey}' failed on attempt {Attempts}; the next attempt is due at {NextRunAt}")]
    private static partial void LogRetryScheduled(
        ILogger logger, string jobKey, int attempts, DateTimeOffset nextRunAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job run '{JobKey}' failed on attempt {Attempts} and has no attempts left; marked it failed")]
    private static partial void LogAttemptsExhausted(ILogger logger, string jobKey, int attempts);

    /// <summary>
    /// This host no longer held the run's claim when its attempt ended, so it recorded nothing: no
    /// history, no cursor, no retry. EventId 410 is a contract: never renumber it.
    /// </summary>
    [LoggerMessage(
        EventId = 410,
        EventName = "ClaimLost",
        Level = LogLevel.Warning,
        Message = "Lost the claim on job run '{JobKey}': another host took it over after the claim timeout, or this " +
            "host could not renew it for that long, so this attempt's outcome was not recorded")]
    private static partial void LogClaimLost(ILogger logger, string jobKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outcome of job run '{JobKey}' could not be recorded; its claim will go stale and another host will recover it")]
    private static partial void LogOutcomeNotRecorded(ILogger logger, string jobKey, Exception exception);
}
