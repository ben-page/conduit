using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// The non-durable scheduler: one timer loop per declared schedule, dispatching straight through
/// <see cref="IMediator"/>. Used when no persistence engine is registered.
/// </summary>
/// <remarks>
/// There is nothing to coordinate through, so the schedule fires on <b>every</b> host. That is a
/// materially weaker guarantee than the durable path, so it is logged as a Warning per schedule at
/// startup rather than left for someone to discover from duplicated work. All time comes from the
/// injected <see cref="TimeProvider"/>, never the ambient clock, so occurrence computation is
/// deterministically testable.
/// </remarks>
internal sealed partial class InMemoryJobScheduler : BackgroundService
{
    private readonly ScheduleRegistry registry;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<InMemoryJobScheduler> logger;
    private readonly JobSchedulerReadiness readiness;
    private readonly InMemoryJobState state;
    private readonly DateTimeOffset startedAt;

    public InMemoryJobScheduler(
        ScheduleRegistry registry,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        JobSchedulerReadiness readiness,
        InMemoryJobState state,
        ILogger<InMemoryJobScheduler> logger)
    {
        this.registry = registry;
        this.scopeFactory = scopeFactory;
        this.timeProvider = timeProvider;
        this.readiness = readiness;
        this.state = state;
        this.logger = logger;

        // Read at construction rather than when the loop first runs: hosted services are constructed
        // before any of them starts, so a schedule's first occurrence is anchored to when the host
        // came up and does not drift with however long startup takes.
        this.startedAt = timeProvider.GetUtcNow();
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.readiness.Expect(this.registry.Schedules.Count);

        var loops = new List<Task>(this.registry.Schedules.Count);
        foreach (var schedule in this.registry.Schedules)
        {
            LogRunsOnEveryHost(this.logger, schedule.Name);
            loops.Add(this.RunScheduleAsync(schedule, stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    private async Task RunScheduleAsync(JobSchedule schedule, CancellationToken stoppingToken)
    {
        var next = schedule.NextOccurrenceAfter(this.startedAt);
        var armed = false;
        var startUpRunPending = schedule.RunsAtStart;

        while (!stoppingToken.IsCancellationRequested && next is { } occurrence)
        {
            // The timer exists the moment Task.Delay is called, so reporting between creating it and
            // awaiting it is the only point at which "armed" is true and not yet racy.
            var delay = occurrence - this.timeProvider.GetUtcNow();
            var wait = delay > TimeSpan.Zero
                ? Task.Delay(delay, this.timeProvider, stoppingToken)
                : Task.CompletedTask;
            armed = ReportArmedOnce(this.readiness, armed);

            if (startUpRunPending)
            {
                // Dispatched with the first occurrence's timer already armed, so readiness never waits
                // on this run and the timetable keeps the start instant's phase. Awaited like any
                // occurrence: this loop never overlaps itself, so a start-up run longer than the
                // interval delays the first occurrence, as a long occurrence delays the next.
                startUpRunPending = false;
                await this.DispatchAsync(schedule, occurrence: null, stoppingToken);
            }

            try
            {
                await wait;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await this.DispatchAsync(schedule, occurrence, stoppingToken);
            next = schedule.NextOccurrenceAfter(occurrence);
        }
    }

    /// <summary>Dispatches one run: an occurrence, or the start-up run when <paramref name="occurrence"/> is null.</summary>
    private async Task DispatchAsync(
        JobSchedule schedule, DateTimeOffset? occurrence, CancellationToken stoppingToken)
    {
        if (this.state.IsPaused(schedule.Name))
        {
            return;
        }

        await using var scope = this.scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Cancellation still works without a database — it just cannot reach beyond this host. The
        // token is linked to shutdown and, when declared, to a MaxRuntime source, which needs nothing
        // more than a start time and a token. Linking rather than cancelling from a timer callback
        // matters: a bare timer can fire after the job has finished and its source is disposed, and
        // the resulting ObjectDisposedException on a timer thread is unhandled.
        using var runtimeLimit = schedule.MaxRuntime is { } limit
            ? new CancellationTokenSource(limit, this.timeProvider)
            : null;
        using var jobCancellation = runtimeLimit is null
            ? CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
            : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runtimeLimit.Token);

        // This loop awaits each run before the next occurrence, so it never overlaps itself; what it
        // can overlap is a manual run, which registers in the same place.
        var tracked = this.state.TryTrack(schedule.Name, jobCancellation);
        if (!tracked && !schedule.AllowsOverlap)
        {
            if (occurrence is { } due)
            {
                LogOverlapSkipped(this.logger, schedule.Name, due);
            }
            else
            {
                LogStartUpRunOverlapSkipped(this.logger, schedule.Name);
            }

            return;
        }

        try
        {
            await mediator.ExecuteAsync(schedule.CreateCommand(), jobCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Draining, cancelled, or past MaxRuntime. There is no ledger to release, so the
            // occurrence is simply not run here.
        }
#pragma warning disable CA1031 // A failing job must not tear down the scheduler for every other schedule.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogJobFailed(this.logger, schedule.Name, exception);
        }
        finally
        {
            if (tracked)
            {
                this.state.Forget(schedule.Name, jobCancellation);
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job schedule '{ScheduleName}' is running without a persistence engine, so it will " +
                  "fire on every host. Register an engine (for example conduit.UseSqlite(connectionString)) " +
                  "for cluster-wide exactly-once scheduling.")]
    private static partial void LogRunsOnEveryHost(ILogger logger, string scheduleName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job schedule '{ScheduleName}' failed")]
    private static partial void LogJobFailed(ILogger logger, string scheduleName, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' skipped occurrence {Occurrence} because a run of it is still in progress on this host")]
    private static partial void LogOverlapSkipped(
        ILogger logger, string scheduleName, DateTimeOffset occurrence);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job schedule '{ScheduleName}' skipped its start-up run because a run of it is still in progress on this host")]
    private static partial void LogStartUpRunOverlapSkipped(ILogger logger, string scheduleName);
}
