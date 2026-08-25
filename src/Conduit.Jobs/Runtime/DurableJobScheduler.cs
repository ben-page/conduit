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

    private async Task MaterializeAndClaimAsync(
        JobSchedule schedule, DateTimeOffset occurrence, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

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
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var attempt = await this.runner.ExecuteAsync(
            run, () => false, stoppingToken, jobCancellation.Token);

        await this.RecordOutcomeAsync(run, attempt);
    }

    private async Task RecordOutcomeAsync(JobRun run, JobAttempt attempt)
    {
        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

            switch (attempt.Outcome)
            {
                case JobOutcome.Completed:
                    await store.CompleteAsync(run.Id, run.ScheduleName, run.Occurrence, CancellationToken.None);
                    break;

                case JobOutcome.ClaimLost:
                    // A handover, not an attempt: the count is untouched so the next host starts
                    // where this one left off rather than one attempt closer to giving up.
                    await store.ReleaseAsync(run.Id, CancellationToken.None);
                    break;

                case JobOutcome.Failed:
                case JobOutcome.Cancelled:
                default:
                    await store.ReleaseAsync(run.Id, CancellationToken.None);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Job claim sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outcome of job run '{JobKey}' could not be recorded; its claim will go stale and another host will recover it")]
    private static partial void LogOutcomeNotRecorded(ILogger logger, string jobKey, Exception exception);
}
