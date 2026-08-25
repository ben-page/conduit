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

    public InMemoryJobScheduler(
        ScheduleRegistry registry,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<InMemoryJobScheduler> logger)
    {
        this.registry = registry;
        this.scopeFactory = scopeFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every loop must register its first timer before this method yields, or a test that
        // advances a FakeTimeProvider immediately after the host starts would advance past an
        // occurrence no timer was waiting for yet. Starting them without an intervening await is
        // what guarantees that.
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
        var next = schedule.NextOccurrenceAfter(this.timeProvider.GetUtcNow());

        while (!stoppingToken.IsCancellationRequested && next is { } occurrence)
        {
            var delay = occurrence - this.timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, this.timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await this.DispatchAsync(schedule, stoppingToken);
            next = schedule.NextOccurrenceAfter(occurrence);
        }
    }

    private async Task DispatchAsync(JobSchedule schedule, CancellationToken stoppingToken)
    {
        await using var scope = this.scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        try
        {
            await mediator.ExecuteAsync(schedule.CreateCommand(), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Draining. There is no ledger to release, so the occurrence is simply not run here.
        }
#pragma warning disable CA1031 // A failing job must not tear down the scheduler for every other schedule.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogJobFailed(this.logger, schedule.Name, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job schedule '{ScheduleName}' is running without a persistence engine, so it will " +
                  "fire on every host. Register an engine (for example conduit.UseSqlite(connectionString)) " +
                  "for cluster-wide exactly-once scheduling.")]
    private static partial void LogRunsOnEveryHost(ILogger logger, string scheduleName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job schedule '{ScheduleName}' failed")]
    private static partial void LogJobFailed(ILogger logger, string scheduleName, Exception exception);
}
