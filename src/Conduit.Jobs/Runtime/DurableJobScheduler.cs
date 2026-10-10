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
    private const int ScopeBatchSize = 32;

    // A schedule down for long enough to miss this many occurrences has no meaningful catch-up left
    // to run, and walking every one of them would stall startup.
    private const int MaxMisfireWalk = 100_000;

    private readonly ScheduleRegistry registry;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ClaimedJobExecutor executor;
    private readonly TimeProvider timeProvider;
    private readonly IOptions<JobsOptions> options;
    private readonly ILogger<DurableJobScheduler> logger;
    private readonly JobSchedulerReadiness readiness;
    private readonly DateTimeOffset startedAt;

    public DurableJobScheduler(
        ScheduleRegistry registry,
        IServiceScopeFactory scopeFactory,
        ClaimedJobExecutor executor,
        TimeProvider timeProvider,
        IOptions<JobsOptions> options,
        JobSchedulerReadiness readiness,
        ILogger<DurableJobScheduler> logger)
    {
        this.registry = registry;
        this.scopeFactory = scopeFactory;
        this.executor = executor;
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
        await this.executor.DrainAsync();
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

            var row = await store.GetScheduleAsync(schedule.Name, stoppingToken);
            if (row is { IsPaused: true })
            {
                // Skipped, and the cursor is left alone: resuming should pick up from the next
                // occurrence, and a paused schedule that had silently advanced its cursor would look
                // to a later misfire evaluation like it had run everything.
                LogPausedSkip(this.logger, schedule.Name, occurrence);
                return;
            }

            // N replicas race here and one row results. The claim that follows then elects the single
            // host that runs it.
            if (schedule.AllowsOverlap)
            {
                // No lock to read the cursor under, so it is read just before the insert: a replica late
                // by more than that instant skips; one late by less can still re-run the occurrence.
                if (row?.LastCompletedOccurrence is { } cursor && occurrence <= cursor)
                {
                    LogAlreadyCompleted(this.logger, schedule.Name, occurrence);
                    return;
                }

                await store.TryMaterializeAsync(BuildRun(schedule, occurrence), stoppingToken);
            }
            else
            {
                switch (await store.TryMaterializeWithoutOverlapAsync(BuildRun(schedule, occurrence), stoppingToken))
                {
                    case MaterializeOutcome.AlreadyCompleted:
                        // This host's timer fired after another replica had already run the occurrence
                        // to the end: the row is gone, and only the cursor, read under the lock, says so.
                        LogAlreadyCompleted(this.logger, schedule.Name, occurrence);
                        return;

                    case MaterializeOutcome.Overlapping:
                        // Running, or awaiting a retry — a previous occurrence or a manual run: either
                        // way starting a second is the overlap the policy exists to prevent. The check
                        // and the insert are one locked step shared with trigger-now, so neither path
                        // can slip a run in between the other's check and its insert. Bounded, because
                        // attempts are finite — a run that keeps failing becomes terminal and stops
                        // holding the schedule up.
                        LogOverlapSkipped(this.logger, schedule.Name, occurrence);
                        return;

                    case MaterializeOutcome.Materialized:
                    case MaterializeOutcome.AlreadyMaterialized:
                    default:
                        break;
                }
            }
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
                await this.PickUpDueScopesAsync(stoppingToken);

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
    /// The sweep sleeps until the earliest pending run or waiting scope is due, capped at the claim
    /// timeout so a claim abandoned by a crashed host is still recovered when nothing else is
    /// scheduled. Scopes must be in this minimum: without them a delayed coalescing trigger would
    /// wait out the whole claim timeout past its due time.
    /// </summary>
    /// <remarks>
    /// A scope that is due but blocked behind an in-flight run for the same key is left alone by
    /// <see cref="PickUpDueScopesAsync"/> — its coalesce key is still held, so the run cannot
    /// materialize — but the scope's due time has already passed and stays in the past. That makes
    /// <c>untilDue</c> zero or negative, which fails the "still in the future" check below and falls
    /// through to the ceiling, exactly as it does when nothing at all is due. This is deliberate: the
    /// alternative, treating a blocked scope's past due time as "wake up immediately," would spin the
    /// sweep at zero delay on work it cannot yet take. The cost is that nothing nudges the sweep when
    /// the blocking run finally clears, so the follow-on run starts only on the next sweep pass — up to
    /// <c>ClaimTimeout</c> after the run ahead of it clears. That latency is a known, accepted
    /// limitation; no nudge mechanism or shorter poll is added to close it.
    /// </remarks>
    private async Task<TimeSpan> ComputeSweepDelayAsync(CancellationToken stoppingToken)
    {
        var ceiling = this.options.Value.ClaimTimeout;

        await using var scope = this.scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var nextRun = await store.GetNextPendingDueAsync(stoppingToken);
        var nextScope = await store.GetNextScopeDueAsync(stoppingToken);

        var nextDue = (nextRun, nextScope) switch
        {
            ({ } run, { } waiting) => run < waiting ? run : waiting,
            ({ } run, null) => run,
            (null, { } waiting) => waiting,
            _ => (DateTimeOffset?)null,
        };

        if (nextDue is not { } due)
        {
            return ceiling;
        }

        var untilDue = due - this.timeProvider.GetUtcNow();
        return untilDue > TimeSpan.Zero && untilDue < ceiling ? untilDue : ceiling;
    }

    /// <summary>
    /// Turns due coalescing scopes into runs. Runs before the claim in each sweep so a scope that has
    /// just come due is claimed in the same pass rather than waiting for the next one — which is also
    /// why each pickup is isolated: an exception escaping here would abort the claim behind it and
    /// idle the whole sweep for a full <c>ClaimTimeout</c>.
    /// </summary>
    private async Task PickUpDueScopesAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await using var scope = this.scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var coalescer = scope.ServiceProvider.GetRequiredService<ScopeCoalescer>();
        var now = this.timeProvider.GetUtcNow();

        var due = await store.GetDueScopesAsync(now, ScopeBatchSize, stoppingToken);
        foreach (var pending in due)
        {
            try
            {
                await coalescer.PickUpAsync(pending, now, runInline: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One scope's database error must not stop every other scope, nor the claim.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogScopePickUpFailed(this.logger, pending.ScopeKey, exception);
            }
        }
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
            this.StartRun(run);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Runs a claimed job without blocking the loop that claimed it, so a long job never delays the
    /// next occurrence or the next sweep. Overlap is governed by the overlap check, not by this.
    /// </summary>
    private void StartRun(JobRun run)
        => this.executor.Start(run);

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
        Message = "Job schedule '{ScheduleName}' is paused, so occurrence {Occurrence} was skipped")]
    private static partial void LogPausedSkip(
        ILogger logger, string scheduleName, DateTimeOffset occurrence);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' skipped occurrence {Occurrence} because another run of it is still outstanding")]
    private static partial void LogOverlapSkipped(
        ILogger logger, string scheduleName, DateTimeOffset occurrence);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' skipped occurrence {Occurrence} because another host has already completed it")]
    private static partial void LogAlreadyCompleted(
        ILogger logger, string scheduleName, DateTimeOffset occurrence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job schedule '{ScheduleName}' database access failed")]
    private static partial void LogScheduleStoreFailed(
        ILogger logger, string scheduleName, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job claim sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Coalescing scope '{ScopeKey}' could not be picked up into a run")]
    private static partial void LogScopePickUpFailed(
        ILogger logger, string scopeKey, Exception exception);

}
