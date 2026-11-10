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
/// Two kinds of loop run here. One per timed schedule waits until its next occurrence and
/// materializes it; a single sweep claims whatever is due — including a retry, a delayed event
/// trigger, a manual run, and the occurrence a crashed host abandoned — and waits until the next
/// pending run is due rather than polling. An on-demand schedule gets no loop of its own: its only
/// runs are manual ones, which the sweep claims. All time comes from the injected
/// <see cref="TimeProvider"/>.
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
    private readonly JobSweepSignal sweepSignal;
    private readonly IHostApplicationLifetime lifetime;
    private readonly DateTimeOffset startedAt;

    public DurableJobScheduler(
        ScheduleRegistry registry,
        IServiceScopeFactory scopeFactory,
        ClaimedJobExecutor executor,
        TimeProvider timeProvider,
        IOptions<JobsOptions> options,
        JobSchedulerReadiness readiness,
        JobSweepSignal sweepSignal,
        IHostApplicationLifetime lifetime,
        ILogger<DurableJobScheduler> logger)
    {
        this.registry = registry;
        this.scopeFactory = scopeFactory;
        this.executor = executor;
        this.timeProvider = timeProvider;
        this.options = options;
        this.readiness = readiness;
        this.sweepSignal = sweepSignal;
        this.lifetime = lifetime;
        this.logger = logger;

        // Read at construction rather than when a loop first runs: hosted services are constructed
        // before any of them starts, so a schedule with no cursor yet waits for its first occurrence
        // after the host came up, not after however long startup takes. The instant picks which
        // occurrence comes first, never when occurrences fall: cron is absolute and an interval aligns
        // to the Unix epoch, so replicas started at different times agree.
        this.startedAt = timeProvider.GetUtcNow();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every running job's token is linked to the host's stopping token, so it was cancelled when the stop
    /// began; this waits for the jobs to end, but no longer than <paramref name="cancellationToken"/>, the
    /// host's shutdown deadline. A handler that ignores its token would otherwise hold the host's stop open
    /// past <c>ShutdownTimeout</c>, for as long as it ran.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // In-flight runs are not part of ExecuteAsync's task, so without this a draining host would
        // exit while still holding claims — exactly the stall that releasing them is meant to avoid.
        try
        {
            await this.executor.DrainAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Left running, claims unreleased: each goes stale once this host stops renewing it, and another
            // host takes the run over after ClaimTimeout. Read after the deadline, so runs that ended in the
            // meantime are not named; if none is left, nothing was abandoned.
            var abandoned = this.executor.RunningJobKeys;
            if (abandoned.Count > 0)
            {
                LogDrainAbandoned(this.logger, abandoned);
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The loops stop at the host's first stop signal, ApplicationStopping, not only at this service's own
        // StopAsync, which the host reaches later (after stopping the services registered after this one). Running
        // jobs are linked to ApplicationStopping, so between the two a drained run's release woke the sweep, which
        // re-claimed the run and started its handler again under a cancelled token, only to hand it back again:
        // hundreds of times in half a second (ClaimRenewalShould.NotStartARunOnceTheHostIsStopping).
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, this.lifetime.ApplicationStopping);

        // An on-demand schedule has no timetable, so it gets no loop: no cursor read, no misfire walk,
        // no occurrence and no start-up run. Its schedule row (reconciled by the initializer) holds only
        // its pause flag and a cursor nothing moves, since a manual run is not an occurrence: null
        // unless the schedule was once declared with a timetable.
        var timed = this.registry.Schedules.Where(schedule => !schedule.IsOnDemand).ToList();
        this.readiness.Expect(timed.Count + 1);

        var loops = new List<Task>(timed.Count + 1);
        foreach (var schedule in timed)
        {
            loops.Add(this.RunScheduleAsync(schedule, stopping.Token));
        }

        loops.Add(this.SweepAsync(stopping.Token));
        await Task.WhenAll(loops);
    }

    private async Task RunScheduleAsync(JobSchedule schedule, CancellationToken stoppingToken)
    {
        // With no cursor, the first occurrence after this host's start. Any instant gives the same
        // timetable (JobSchedule.NextOccurrenceAfter), so no replica needs another's cursor to agree.
        var cursor = await this.ReadCursorAsync(schedule.Name, stoppingToken) ?? this.startedAt;
        cursor = await this.ApplyMisfireAsync(schedule, cursor, stoppingToken) ?? cursor;

        // After the misfire walk, so the cursor is settled exactly as it would be without this, and
        // before the first wait, as an occurrence already due is. The timetable below still follows
        // the cursor: the start-up run is not an occurrence and does not shift the phase.
        if (schedule.RunsAtStart)
        {
            await this.MaterializeStartUpRunAndClaimAsync(schedule, stoppingToken);
        }

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
                // No lock to read the cursor under, so it comes from the schedule row read above. A late
                // replica re-runs the occurrence only if another replica's completion of it commits
                // between that read and the insert below: earlier, the insert collapses into the row;
                // later, the read sees the cursor. The cursor only moves forward, so runs finishing out
                // of order cannot reopen that gap.
                if (row?.LastCompletedOccurrence is { } cursor && occurrence <= cursor)
                {
                    LogCursorPassed(this.logger, schedule.Name, occurrence);
                    return;
                }

                await store.TryMaterializeAsync(BuildRun(schedule, occurrence), stoppingToken);
            }
            else
            {
                switch (await store.TryMaterializeWithoutOverlapAsync(BuildRun(schedule, occurrence), stoppingToken))
                {
                    case MaterializeOutcome.AlreadyCompleted:
                        // The cursor, read under the lock, is already at or past this occurrence: another
                        // replica took it to a terminal outcome before this host's timer fired, or a host
                        // starting up counted it as missed and skipped past it (MisfirePolicy.Skip).
                        LogCursorPassed(this.logger, schedule.Name, occurrence);
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

        await this.ClaimQueuedRunAsync(schedule.Name, stoppingToken);
    }

    /// <summary>
    /// Writes a <see cref="JobSchedule.RunsAtStart"/> schedule's one start-up run and claims it. The
    /// run is written the way <see cref="IJobs.TriggerNowAsync"/> writes a manual run: no occurrence,
    /// so it never moves the cursor and the cursor never refuses it, and through the same overlap
    /// lock. Its key is the start instant, so a second write for the same start collapses into the
    /// first while that run is outstanding — the guarantee trigger-now's key gives, no more: a
    /// completed schedule run's key is not retained.
    /// </summary>
    private async Task MaterializeStartUpRunAndClaimAsync(JobSchedule schedule, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

            var row = await store.GetScheduleAsync(schedule.Name, stoppingToken);
            if (row is { IsPaused: true })
            {
                // Unlike trigger-now, nobody asked for this run, so a pause holds it back like an
                // occurrence. It is not deferred to the resume: the next start writes its own.
                LogStartUpRunPaused(this.logger, schedule.Name);
                return;
            }

            var run = this.BuildStartUpRun(schedule);
            if (schedule.AllowsOverlap)
            {
                await store.TryMaterializeAsync(run, stoppingToken);
            }
            else if (await store.TryMaterializeWithoutOverlapAsync(run, stoppingToken)
                     == MaterializeOutcome.Overlapping)
            {
                // A run already outstanding — a retry left from before the restart, a manual run, a
                // replica's start-up run — is this start's work; a second would be the overlap the
                // policy exists to prevent.
                LogStartUpRunOverlapSkipped(this.logger, schedule.Name);
                return;
            }

            // Materialized, or AlreadyMaterialized by a start at the same instant whose run is still
            // outstanding: either way the run with this key is queued. A null occurrence is never
            // AlreadyCompleted.
            LogStartUpRunQueued(this.logger, schedule.Name, run.JobKey);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A database error at start must not stop the schedule's timetable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogStartUpRunFailed(this.logger, schedule.Name, exception);
            return;
        }

        await this.ClaimQueuedRunAsync(schedule.Name, stoppingToken);
    }

    /// <summary>
    /// Claims what is due straight after a schedule's loop has queued a run, so the run starts without
    /// waiting for the sweep. A store error here (a deadlock victim among replicas claiming at once,
    /// say) is logged rather than let escape: escaping, it would end this schedule's loop for good. The
    /// run stays queued and the sweep claims it — on its next pass when nothing was claimed, or once
    /// the claim goes stale after <c>ClaimTimeout</c> when the claim committed and only reading it
    /// back failed.
    /// </summary>
    private async Task ClaimQueuedRunAsync(string scheduleName, CancellationToken stoppingToken)
    {
        try
        {
            await this.ClaimAndRunDueAsync(stoppingToken);
        }
#pragma warning disable CA1031 // While stopping, any exception is the stop's cancellation: SqlClient reports it as a SqlException.
        catch (Exception) when (stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // Draining.
        }
#pragma warning disable CA1031 // A claim-time database error must not end the schedule's loop; the sweep claims the run.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogQueuedRunClaimFailed(this.logger, scheduleName, exception);
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        var armed = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Armed before the pass reads anything, so work written while the pass runs — after it has looked —
            // still wakes the sleep that follows, and work written before is in what the pass reads.
            var woken = this.sweepSignal.Arm();

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

            // Sleeps until the earliest due work or a wake from this process (JobSweepSignal), whichever is first.
            // The timer is dropped when the wake wins, so sleeps do not pile up.
            using var sleep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var wait = Task.Delay(delay, this.timeProvider, sleep.Token);
            armed = ReportArmedOnce(this.readiness, armed);

            await Task.WhenAny(wait, woken);
            await sleep.CancelAsync();
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
    /// wait out the whole claim timeout past its due time. Work this process writes after the sleep was
    /// computed wakes it early (<see cref="JobSweepSignal"/>).
    /// </summary>
    /// <remarks>
    /// A scope that is due but blocked behind an in-flight run for the same key is left alone by
    /// <see cref="PickUpDueScopesAsync"/> — its coalesce key is still held, so the run cannot
    /// materialize — but the scope's due time has already passed and stays in the past. That makes
    /// <c>untilDue</c> zero or negative, which fails the "still in the future" check below and falls
    /// through to the ceiling, exactly as it does when nothing at all is due. This is deliberate: the
    /// alternative, treating a blocked scope's past due time as "wake up immediately," would spin the
    /// sweep at zero delay on work it cannot yet take. What ends the wait is the blocking run's end: the
    /// executor wakes the sweep of the host that ran it, whose next pass picks the scope up. Only when that
    /// run's outcome is not recorded (its host died, or lost its claim) does the follow-on wait for a sweep
    /// pass, up to <c>ClaimTimeout</c>.
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
#pragma warning disable CA1031 // While stopping, any exception is the stop's cancellation: SqlClient reports it as a SqlException.
        catch (Exception) when (stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // Untested: this read comes before the loop reports armed, so a fault held open until the
            // stop would hold the host's readiness too.
            return null;
        }
#pragma warning disable CA1031 // A database error at start must not end the schedule's loop before it has begun.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Read as a schedule with no cursor yet, rather than end the loop: it waits for the first
            // occurrence after this host's start. The cost: that start skips its misfire policy (no
            // RunImmediately catch-up, no Skip cursor move). The timetable is unchanged, since cron is
            // absolute and an interval aligns to the Unix epoch. A completed run still cannot re-run:
            // every occurrence's write checks the real cursor (under the overlap lock, or from the
            // schedule row for AllowOverlap()).
            LogCursorReadFailed(this.logger, scheduleName, exception);
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

    /// <summary>
    /// A start-up run: a manual run's shape (no occurrence, due now) under a key derived from the
    /// scheduler's construction instant rather than from the clock at the write.
    /// </summary>
    private JobRun BuildStartUpRun(JobSchedule schedule)
    {
        var command = schedule.CreateCommand();
        return new JobRun
        {
            JobKey = BuildStartUpJobKey(schedule.Name, this.startedAt),
            ScheduleName = schedule.Name,
            Occurrence = null,
            MessageType = command.GetType().FullName!,
            Payload = JsonSerializer.Serialize(command, command.GetType(), ConduitRuntime.JsonOptions),
            RunAt = this.timeProvider.GetUtcNow(),
            Status = JobRunStatus.Pending,
        };
    }

    /// <summary>
    /// The start-up run's key, <c>{schedule}@start-{startedAt}</c>. One start computes one key, so a
    /// second write for the same start collapses into the first while that run is outstanding (a
    /// completed schedule run's key is not retained). Hosts started at different instants write
    /// different keys; for a no-overlap schedule the overlap lock keeps at most one outstanding.
    /// </summary>
    internal static string BuildStartUpJobKey(string scheduleName, DateTimeOffset startedAt)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{scheduleName}@start-{startedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");

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
        Message = "Job schedule '{ScheduleName}' skipped occurrence {Occurrence} because its cursor is already at or past it: another host has taken it to a terminal outcome, or a host starting up skipped past it as missed")]
    private static partial void LogCursorPassed(
        ILogger logger, string scheduleName, DateTimeOffset occurrence);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' runs at start: run '{JobKey}' is queued")]
    private static partial void LogStartUpRunQueued(ILogger logger, string scheduleName, string jobKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' wrote no start-up run because another run of it is still outstanding")]
    private static partial void LogStartUpRunOverlapSkipped(ILogger logger, string scheduleName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' is paused, so its start-up run was skipped")]
    private static partial void LogStartUpRunPaused(ILogger logger, string scheduleName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job schedule '{ScheduleName}' could not write its start-up run")]
    private static partial void LogStartUpRunFailed(ILogger logger, string scheduleName, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job schedule '{ScheduleName}' could not claim its queued run; the sweep will pick it up")]
    private static partial void LogQueuedRunClaimFailed(ILogger logger, string scheduleName, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job schedule '{ScheduleName}' could not read its cursor, so its timetable starts from this host's start and its misfire policy is not applied")]
    private static partial void LogCursorReadFailed(ILogger logger, string scheduleName, Exception exception);

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

    /// <summary>
    /// The host's shutdown deadline passed with runs still executing, so the stop returned without them. EventId
    /// 411 is a contract: never renumber it.
    /// </summary>
    [LoggerMessage(
        EventId = 411,
        EventName = "DrainAbandoned",
        Level = LogLevel.Warning,
        Message = "The shutdown deadline passed while job runs {JobKeys} were still executing; they were left running " +
            "with their claims unreleased, so another host takes each over once its claim goes stale after the claim timeout")]
    private static partial void LogDrainAbandoned(ILogger logger, IReadOnlyList<string> jobKeys);
}
