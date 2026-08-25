using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// The durable scheduler. Every host computes the same occurrences in memory from the same
/// declarations, so the database is never asked <i>when</i> a job is due — only <i>who runs this
/// one</i>, which one atomic row claim answers.
/// </summary>
/// <remarks>
/// Two kinds of loop run here. One per schedule waits until its next occurrence and materializes it;
/// a single sweep claims whatever is due — including a retry, a delayed event trigger, and the
/// occurrence a crashed host abandoned — and waits until the next pending run is due rather than
/// polling. All time comes from the injected <see cref="TimeProvider"/>.
/// </remarks>
internal sealed partial class DurableJobScheduler : BackgroundService
{
    private const int ClaimBatchSize = 32;

    // A schedule down for long enough to miss this many occurrences has no meaningful catch-up left
    // to run, and walking every one of them would stall startup.
    private const int MaxMisfireWalk = 100_000;

    private readonly ScheduleRegistry registry;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly JobRunner runner;
    private readonly TimeProvider timeProvider;
    private readonly IOptions<JobsOptions> options;
    private readonly ILogger<DurableJobScheduler> logger;
    private readonly JobSchedulerReadiness readiness;
    private readonly ConcurrentDictionary<long, Task> inFlight = new();
    private readonly DateTimeOffset startedAt;

    public DurableJobScheduler(
        ScheduleRegistry registry,
        IServiceScopeFactory scopeFactory,
        JobRunner runner,
        TimeProvider timeProvider,
        IOptions<JobsOptions> options,
        JobSchedulerReadiness readiness,
        ILogger<DurableJobScheduler> logger)
    {
        this.registry = registry;
        this.scopeFactory = scopeFactory;
        this.runner = runner;
        this.timeProvider = timeProvider;
        this.options = options;
        this.readiness = readiness;
        this.logger = logger;

        // Read at construction rather than when a loop first runs: hosted services are constructed
        // before any of them starts, so a schedule with no cursor yet is anchored to when the host
        // came up and does not drift with however long startup takes.
        this.startedAt = timeProvider.GetUtcNow();
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // In-flight runs are not part of ExecuteAsync's task, so without this a draining host would
        // exit while still holding claims — exactly the stall that releasing them is meant to avoid.
        await Task.WhenAll(this.inFlight.Values);
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.readiness.Expect(this.registry.Schedules.Count + 1);

        var loops = new List<Task>(this.registry.Schedules.Count + 1);
        foreach (var schedule in this.registry.Schedules)
        {
            loops.Add(this.RunScheduleAsync(schedule, stoppingToken));
        }

        loops.Add(this.SweepAsync(stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task RunScheduleAsync(JobSchedule schedule, CancellationToken stoppingToken)
    {
        var cursor = await this.ReadCursorAsync(schedule.Name, stoppingToken) ?? this.startedAt;
        cursor = await this.ApplyMisfireAsync(schedule, cursor, stoppingToken) ?? cursor;

        var next = schedule.NextOccurrenceAfter(cursor);
        var armed = false;

        while (!stoppingToken.IsCancellationRequested && next is { } occurrence)
        {
            var delay = occurrence - this.timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                // The timer exists the moment Task.Delay is called, so reporting between creating it
                // and awaiting it is the only point at which "armed" is true and not yet racy.
                var wait = Task.Delay(delay, this.timeProvider, stoppingToken);
                armed = ReportArmedOnce(this.readiness, armed);

                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            else
            {
                armed = ReportArmedOnce(this.readiness, armed);
            }

            await this.MaterializeAndClaimAsync(schedule, occurrence, stoppingToken);
            next = schedule.NextOccurrenceAfter(occurrence);
        }
    }

    /// <summary>
    /// Settles what happens to occurrences that fell between the cursor and now — the ones missed
    /// while the application was not running. Neither policy backfills: replaying a long outage would
    /// produce a burst of runs whose moment has passed, which for most jobs is worse than missing
    /// them.
    /// </summary>
    /// <returns>The most recent missed occurrence, or <see langword="null"/> when none were missed.</returns>
    private async Task<DateTimeOffset?> ApplyMisfireAsync(
        JobSchedule schedule, DateTimeOffset cursor, CancellationToken stoppingToken)
    {
        var now = this.timeProvider.GetUtcNow();
        DateTimeOffset? lastMissed = null;
        var walked = 0;

        var candidate = schedule.NextOccurrenceAfter(cursor);
        while (candidate is { } occurrence && occurrence <= now && walked < MaxMisfireWalk)
        {
            lastMissed = occurrence;
            walked++;
            candidate = schedule.NextOccurrenceAfter(occurrence);
        }

        if (lastMissed is not { } mostRecent)
        {
            return null;
        }

        if (walked >= MaxMisfireWalk)
        {
            LogMisfireTruncated(this.logger, schedule.Name, MaxMisfireWalk);
            await this.RunOnStoreAsync(
                store => store.AdvanceCursorAsync(schedule.Name, now, stoppingToken),
                schedule.Name,
                stoppingToken);
            return now;
        }

        if (schedule.Misfire == MisfirePolicy.RunImmediately)
        {
            // Exactly one catch-up, for the most recent missed occurrence. There is no backfill.
            LogMisfireCatchUp(this.logger, schedule.Name, mostRecent, walked);
            await this.MaterializeAndClaimAsync(schedule, mostRecent, stoppingToken);
            return mostRecent;
        }

        LogMisfireSkipped(this.logger, schedule.Name, walked);
        await this.RunOnStoreAsync(
            store => store.AdvanceCursorAsync(schedule.Name, mostRecent, stoppingToken),
            schedule.Name,
            stoppingToken);
        return mostRecent;
    }

    private async Task MaterializeAndClaimAsync(
        JobSchedule schedule, DateTimeOffset occurrence, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

            if (!schedule.AllowsOverlap
                && await store.HasOutstandingAsync(schedule.Name, stoppingToken))
            {
                // Running, or awaiting a retry: either way the previous occurrence has not succeeded,
                // and starting a second is the overlap the policy exists to prevent. Bounded, because
                // attempts are finite — an occurrence that keeps failing becomes terminal and stops
                // holding the schedule up.
                LogOverlapSkipped(this.logger, schedule.Name, occurrence);
                return;
            }

            // N replicas race here and one row results. The claim that follows then elects the single
            // host that runs it.
            await store.TryMaterializeAsync(BuildRun(schedule, occurrence), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // One schedule's database error must not stop every other schedule.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogOccurrenceFailed(this.logger, schedule.Name, occurrence, exception);
            return;
        }

        await this.ClaimAndRunDueAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        var armed = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                if (await this.ClaimAndRunDueAsync(stoppingToken) > 0)
                {
                    // More may be due in the same instant; go round again before sleeping.
                    continue;
                }

                delay = await this.ComputeSweepDelayAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A transient database error must not kill the sweep for good.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSweepFailed(this.logger, exception);
                delay = this.options.Value.ClaimTimeout;
            }

            var wait = Task.Delay(delay, this.timeProvider, stoppingToken);
            armed = ReportArmedOnce(this.readiness, armed);

            try
            {
                await wait;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool ReportArmedOnce(JobSchedulerReadiness readiness, bool alreadyArmed)
    {
        if (!alreadyArmed)
        {
            readiness.ReportArmed();
        }

        return true;
    }

    /// <summary>
    /// The sweep sleeps until the earliest pending run is due, capped at the claim timeout so a
    /// claim abandoned by a crashed host is still recovered when nothing else is scheduled.
    /// </summary>
    private async Task<TimeSpan> ComputeSweepDelayAsync(CancellationToken stoppingToken)
    {
        var ceiling = this.options.Value.ClaimTimeout;

        await using var scope = this.scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var nextDue = await store.GetNextPendingDueAsync(stoppingToken);

        if (nextDue is not { } due)
        {
            return ceiling;
        }

        var untilDue = due - this.timeProvider.GetUtcNow();
        return untilDue > TimeSpan.Zero && untilDue < ceiling ? untilDue : ceiling;
    }

    private async Task<int> ClaimAndRunDueAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return 0;
        }

        IReadOnlyList<JobRun> claimed;
        await using (var scope = this.scopeFactory.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

            // A fresh token per call: claimed rows are read back by token, so reusing one while an
            // earlier claim is still running would hand this sweep the earlier sweep's rows too.
            claimed = await store.ClaimDueAsync(
                Guid.NewGuid().ToString("N"),
                this.timeProvider.GetUtcNow(),
                ClaimBatchSize,
                stoppingToken);
        }

        foreach (var run in claimed)
        {
            this.StartRun(run, stoppingToken);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Runs a claimed job without blocking the loop that claimed it, so a long job never delays the
    /// next occurrence or the next sweep. Overlap is governed by the overlap check, not by this.
    /// </summary>
    private void StartRun(JobRun run, CancellationToken stoppingToken)
    {
        var task = Task.Run(() => this.RunClaimedAsync(run, stoppingToken), CancellationToken.None);
        this.inFlight[run.Id] = task;
        _ = task.ContinueWith(
            _ => this.inFlight.TryRemove(run.Id, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunClaimedAsync(JobRun run, CancellationToken stoppingToken)
    {
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
            return;
        }

        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

            switch (outcome)
            {
                case JobOutcome.Completed:
                    await store.CompleteAsync(run.Id, run.ScheduleName, run.Occurrence, CancellationToken.None);
                    break;

                case JobOutcome.Cancelled:
                    // Terminal and never retried — retrying would defeat the cancellation. The cursor
                    // still advances, or the occurrence would be re-materialized and cancelling would
                    // loop forever.
                    await store.MarkTerminalAsync(
                        run.Id, JobRunStatus.Cancelled, null,
                        this.CursorScheduleFor(run), run.Occurrence, CancellationToken.None);
                    break;

                case JobOutcome.HandedOver:
                    // A handover, not an attempt: the count is untouched so the next host starts
                    // where this one left off rather than one attempt closer to giving up.
                    await store.ReleaseAsync(run.Id, run.ClaimToken!, CancellationToken.None);
                    break;

                case JobOutcome.Failed:
                    await this.RecordFailureAsync(store, run, error);
                    break;

                case JobOutcome.ClaimLost:
                default:
                    await store.ReleaseAsync(run.Id, run.ClaimToken!, CancellationToken.None);
                    break;
            }
        }
#pragma warning disable CA1031 // Losing the outcome write must not tear the scheduler down; the claim
        catch (Exception exception) // goes stale and another host recovers the run.
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
    private async Task RecordFailureAsync(IJobStore store, JobRun run, Exception? error)
    {
        var message = error?.Message ?? "The job failed without an exception message.";
        var attempts = run.AttemptCount + 1;

        if (attempts >= this.options.Value.MaxAttempts)
        {
            // The cursor advances even though the occurrence never succeeded: one permanently broken
            // occurrence must not block every later one, and the failed row keeps it visible.
            LogAttemptsExhausted(this.logger, run.JobKey, attempts);
            await store.MarkTerminalAsync(
                run.Id, JobRunStatus.Failed, message,
                this.CursorScheduleFor(run), run.Occurrence, CancellationToken.None);
            return;
        }

        var nextRunAt = this.timeProvider.GetUtcNow() + BackoffFor(attempts);
        LogRetryScheduled(this.logger, run.JobKey, attempts, nextRunAt);
        await store.IncrementAttemptAsync(run.Id, nextRunAt, message, CancellationToken.None);
    }

    /// <summary>Exponential backoff from thirty seconds: 1 minute, 2, 4, ...</summary>
    private static TimeSpan BackoffFor(int attempts)
        => TimeSpan.FromSeconds(30 * Math.Pow(2, attempts));

    /// <summary>
    /// The schedule whose cursor this run should advance, or <see langword="null"/> when the run came
    /// from an event trigger and there is no cursor to move.
    /// </summary>
    private string? CursorScheduleFor(JobRun run)
        => this.registry.IsSchedule(run.ScheduleName) ? run.ScheduleName : null;

    private async Task RunOnStoreAsync(
        Func<IJobStore, Task> action, string scheduleName, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IJobStore>());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Draining.
        }
#pragma warning disable CA1031 // One schedule's database error must not stop every other schedule.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogScheduleStoreFailed(this.logger, scheduleName, exception);
        }
    }

    private async Task<DateTimeOffset?> ReadCursorAsync(string scheduleName, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
            var row = await store.GetScheduleAsync(scheduleName, stoppingToken);
            return row?.LastCompletedOccurrence;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static JobRun BuildRun(JobSchedule schedule, DateTimeOffset occurrence)
    {
        var command = schedule.CreateCommand();
        return new JobRun
        {
            JobKey = BuildJobKey(schedule.Name, occurrence),
            ScheduleName = schedule.Name,
            Occurrence = occurrence,
            MessageType = command.GetType().FullName!,
            Payload = JsonSerializer.Serialize(command, command.GetType(), ConduitRuntime.JsonOptions),
            RunAt = occurrence,
            Status = JobRunStatus.Pending,
        };
    }

    /// <summary>
    /// The deterministic run key. Every replica computes the same string for the same occurrence,
    /// which is what makes insert-or-ignore collapse their race into one row.
    /// </summary>
    internal static string BuildJobKey(string scheduleName, DateTimeOffset occurrence)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{scheduleName}@{occurrence.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job schedule '{ScheduleName}' could not materialize occurrence {Occurrence}")]
    private static partial void LogOccurrenceFailed(
        ILogger logger, string scheduleName, DateTimeOffset occurrence, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job run '{JobKey}' failed on attempt {Attempts}; the next attempt is due at {NextRunAt}")]
    private static partial void LogRetryScheduled(
        ILogger logger, string jobKey, int attempts, DateTimeOffset nextRunAt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job run '{JobKey}' failed on attempt {Attempts} and has no attempts left; marking it failed")]
    private static partial void LogAttemptsExhausted(ILogger logger, string jobKey, int attempts);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' skipped {MissedCount} occurrence(s) missed while the application was down")]
    private static partial void LogMisfireSkipped(ILogger logger, string scheduleName, int missedCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' is running one catch-up for {Occurrence}, the most recent of {MissedCount} missed occurrence(s)")]
    private static partial void LogMisfireCatchUp(
        ILogger logger, string scheduleName, DateTimeOffset occurrence, int missedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job schedule '{ScheduleName}' has missed more than {MaxWalk} occurrences; its cursor has been moved to now and no catch-up will run")]
    private static partial void LogMisfireTruncated(ILogger logger, string scheduleName, int maxWalk);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' skipped occurrence {Occurrence} because a previous one is still outstanding")]
    private static partial void LogOverlapSkipped(
        ILogger logger, string scheduleName, DateTimeOffset occurrence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job schedule '{ScheduleName}' database access failed")]
    private static partial void LogScheduleStoreFailed(
        ILogger logger, string scheduleName, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job claim sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outcome of job run '{JobKey}' could not be recorded; its claim will go stale and another host will recover it")]
    private static partial void LogOutcomeNotRecorded(ILogger logger, string jobKey, Exception exception);
}
