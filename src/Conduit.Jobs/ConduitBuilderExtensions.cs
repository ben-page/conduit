using Conduit.Configuration;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conduit.Jobs;

/// <summary>
/// Extension methods for registering the background-jobs subsystem on <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderExtensions
{
    /// <summary>
    /// Adds recurring schedules and event-triggered jobs to the Conduit configuration. Whether jobs
    /// are durable is inferred from whether a persistence engine is registered — an engine means
    /// every schedule fires exactly once across the cluster, no engine means every schedule fires on
    /// every host.
    /// </summary>
    /// <remarks>
    /// A persistence engine must be registered on the builder <b>before</b> this call — durability is
    /// decided here, from what is already in the service collection, so
    /// <c>conduit.UseSqlite(...)</c> after <c>AddJobs(...)</c> would silently leave the schedules
    /// non-durable. That ordering mistake is caught at startup rather than at runtime.
    /// </remarks>
    /// <param name="builder">The Conduit builder to configure.</param>
    /// <param name="configure">Callback declaring schedules, triggers, and tuning.</param>
    /// <returns>The original <paramref name="builder"/> for chaining.</returns>
    public static ConduitBuilder AddJobs(this ConduitBuilder builder, Action<JobsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var jobsBuilder = new JobsBuilder();
        configure(jobsBuilder);

        var options = jobsBuilder.Options;
        if (options.ClaimTimeout <= options.HeartbeatInterval * 2)
        {
            throw new InvalidOperationException(
                $"JobsOptions.ClaimTimeout ({options.ClaimTimeout}) must be more than twice " +
                $"HeartbeatInterval ({options.HeartbeatInterval}). A claim that can expire within two " +
                "heartbeats will be taken from a host that is still running the job, executing it twice.");
        }

        var duplicate = jobsBuilder.Schedules
            .GroupBy(schedule => schedule.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate job schedule name '{duplicate.Key}'. Schedule names are database keys and must be unique.");
        }

        var collision = jobsBuilder.Triggers
            .Select(trigger => trigger.Name)
            .Intersect(jobsBuilder.Schedules.Select(schedule => schedule.Name), StringComparer.Ordinal)
            .FirstOrDefault();
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"'{collision}' names both a job schedule and an event trigger. Both are stored in the " +
                "same column of the run ledger, so a trigger's in-flight run would be mistaken for the " +
                "schedule's and suppress its next occurrence. Rename one of them.");
        }

        builder.Services.AddSingleton(new ScheduleRegistry(jobsBuilder.Schedules, jobsBuilder.Triggers));
        builder.Services.Configure<JobsOptions>(target =>
        {
            target.HeartbeatInterval = options.HeartbeatInterval;
            target.ClaimTimeout = options.ClaimTimeout;
            target.MaxAttempts = options.MaxAttempts;
            target.FailedRetention = options.FailedRetention;
        });

        // Never the ambient clock: every scheduling decision reads this, so occurrence computation,
        // misfire detection, backoff and MaxRuntime stay deterministically testable.
        builder.Services.TryAddSingleton(TimeProvider.System);

        if (HasPersistenceEngine(builder.Services))
        {
            builder.Services.AddScoped<IJobStore, DbJobStore>();
        }
        else
        {
            builder.Services.AddHostedService<InMemoryJobScheduler>();
        }

        builder.Services.AddHostedService<JobTableInitializer>();

        return builder;
    }

    private static bool HasPersistenceEngine(IServiceCollection services)
        => services.Any(descriptor => descriptor.ServiceType == typeof(IConduitConnectionFactory))
            && services.Any(descriptor => descriptor.ServiceType == typeof(ISqlDialect));
}
