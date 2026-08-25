using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs;

/// <summary>
/// Creates both jobs tables on startup and reconciles a cursor row per declared schedule. Fails fast
/// on the two configurations that would otherwise appear to work: an event trigger without an engine
/// (triggers are always durable, so there would be nowhere to write the run) and a misfire policy
/// without an engine (an in-memory timer has no record of what it missed).
/// </summary>
internal sealed class JobTableInitializer : TableInitializerBase
{
    public JobTableInitializer(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
    }

    protected override void ValidateConfiguration(IServiceProvider scopedProvider)
    {
        var registry = scopedProvider.GetRequiredService<ScheduleRegistry>();
        var connectionFactory = scopedProvider.GetService<IConduitConnectionFactory>();
        var dialect = scopedProvider.GetService<ISqlDialect>();

        if (connectionFactory is not null && dialect is not null)
        {
            return;
        }

        if (registry.Triggers.Count > 0)
        {
            throw new InvalidOperationException(
                "Event-triggered jobs require a persistence engine, because they are always durable. " +
                "Register one on the ConduitBuilder (for example conduit.UseSqlite(connectionString)), " +
                "or remove the event triggers. Scheduled jobs run without an engine, but only per-host.");
        }

        var misfiring = registry.Schedules
            .FirstOrDefault(schedule => schedule.Misfire != MisfirePolicy.Skip);
        if (misfiring is not null)
        {
            throw new InvalidOperationException(
                $"Job schedule '{misfiring.Name}' declares a misfire policy, which requires a " +
                "persistence engine. Without one the scheduler is an in-memory timer and has no record " +
                "of occurrences missed while the process was down.");
        }
    }

    protected override async Task CreateTablesAsync(
        IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetService<IJobStore>();
        if (store is null)
        {
            return;
        }

        var registry = scopedProvider.GetRequiredService<ScheduleRegistry>();

        await store.EnsureTablesExistAsync(cancellationToken);
        await store.ReconcileSchedulesAsync(
            [.. registry.Schedules.Select(schedule => schedule.Name)], cancellationToken);
    }
}
