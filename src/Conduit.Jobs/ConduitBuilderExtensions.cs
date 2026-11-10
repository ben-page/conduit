using Conduit.Configuration;
using Conduit.Handlers;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Persistence;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
    /// <para>
    /// <see cref="JobsBuilder.ManageOnly"/> registers <see cref="IJobs"/> without the runner, for a
    /// host that shares the jobs tables but must never execute a job.
    /// </para>
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
        options.Validate();

        // A ceiling of no time cancels every run at its first heartbeat (or, without an engine, at once).
        var unbounded = jobsBuilder.Schedules.Select(schedule => (Kind: "Job schedule", schedule.Name, schedule.MaxRuntime))
            .Concat(jobsBuilder.Triggers.Select(trigger => (Kind: "Event trigger", trigger.Name, trigger.MaxRuntime)))
            .FirstOrDefault(declaration => declaration.MaxRuntime <= TimeSpan.Zero);
        if (unbounded.Name is not null)
        {
            throw new InvalidOperationException(
                $"{unbounded.Kind} '{unbounded.Name}' declares MaxRuntime({unbounded.MaxRuntime}); it must be positive. " +
                "Remove MaxRuntime(...) for a run with no ceiling.");
        }

        var duplicate = jobsBuilder.Schedules
            .GroupBy(schedule => schedule.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate job schedule name '{duplicate.Key}'. Schedule names are database keys and must be unique.");
        }

        var duplicateTrigger = jobsBuilder.Triggers
            .GroupBy(trigger => trigger.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTrigger is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate event trigger name '{duplicateTrigger.Key}'. Trigger names are database keys " +
                "and must be unique: two triggers of one name write the same run key, and the second is " +
                "silently discarded by the idempotent insert. Name them with jobs.On<TEvent>(\"name\").");
        }

        var unmerged = jobsBuilder.Triggers
            .FirstOrDefault(trigger => trigger.CoalesceKey is not null && trigger.Merge is null);
        if (unmerged is not null)
        {
            throw new InvalidOperationException(
                $"Event trigger '{unmerged.Name}' declares CoalesceBy but no Merge. There is no safe " +
                "default: keeping the newest command and discarding the rest narrows the scope, which " +
                "silently loses work. Add .Merge((pending, incoming) => ...) that widens.");
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

        var durable = HasPersistenceEngine(builder.Services);
        if (options.ManageOnly)
        {
            RejectWhatAManageOnlyHostCannotRun(jobsBuilder, durable);
        }

        builder.Services.AddSingleton(new ScheduleRegistry(jobsBuilder.Schedules, jobsBuilder.Triggers));
        // The builder's own instance, registered whole rather than copied property by property: a
        // knob added to JobsOptions and surfaced on JobsBuilder would otherwise have to be added to
        // a copy list too, and forgetting that turns it into a silent no-op that still reads as
        // configured at the call site. AddJobs is the only writer of these options.
        builder.Services.AddSingleton(Options.Create(options));

        // Never the ambient clock: every scheduling decision reads this, so occurrence computation,
        // misfire detection, backoff and MaxRuntime stay deterministically testable.
        builder.Services.TryAddSingleton(TimeProvider.System);
        if (options.ManageOnly)
        {
            // No loop will ever arm, so there is nothing to wait for: readiness is complete from the
            // start, and anything awaiting it does not hang on loops that do not exist.
            builder.Services.AddSingleton(_ =>
            {
                var readiness = new JobSchedulerReadiness();
                readiness.Expect(0);
                return readiness;
            });
        }
        else
        {
            builder.Services.AddSingleton<JobSchedulerReadiness>();
        }

        // Registered first, because hosted services start in registration order and both schedulers
        // read the tables on their very first pass. It also validates, so a misconfiguration fails
        // before any loop has begun. A manage-only host keeps it: reading needs the tables, and
        // pausing a declared schedule needs its row.
        builder.Services.AddHostedService<JobTableInitializer>();

        if (durable)
        {
            builder.Services.AddScoped<IJobStore, DbJobStore>();
            builder.Services.AddScoped<IJobs, DurableJobs>();

            // Before the manage-only cut: DurableJobs wakes it on trigger-now, and with no sweep to hear
            // it a wake does nothing.
            builder.Services.AddSingleton<JobSweepSignal>();
            if (options.ManageOnly)
            {
                // IJobs reads and writes the ledger through IJobStore alone. Everything below is the
                // runner, and a runner here would claim any due run in the shared table — including
                // one whose handler is registered only in the process that owns execution.
                return builder;
            }

            builder.Services.AddScoped<ScopeCoalescer>();
            builder.Services.AddSingleton<JobRunner>();
            builder.Services.AddSingleton<ClaimedJobExecutor>();
            builder.Services.AddHostedService<DurableJobScheduler>();
            builder.Services.AddHostedService<JobCleanup>();

            // One closed listener per declared trigger's event type. The generated mediator resolves
            // listeners from the container at publish time, so a runtime registration is enough to run
            // it -- but the generator never saw it, so a broker transport would not subscribe the event:
            // it is activated in the type registry, which the generated AddConduit registers before this
            // callback runs. (A builder made by hand, as a unit test does, has no registry.)
            var typeRegistry = builder.Services
                .LastOrDefault(descriptor => descriptor.ServiceType == typeof(ConduitTypeRegistry) && !descriptor.IsKeyedService)
                ?.ImplementationInstance as ConduitTypeRegistry;
            foreach (var eventType in jobsBuilder.Triggers
                .Select(trigger => trigger.EventType)
                .Distinct())
            {
                builder.Services.AddTransient(
                    typeof(IEventListener<>).MakeGenericType(eventType),
                    typeof(EventTriggerListener<>).MakeGenericType(eventType));
                typeRegistry?.ActivateEvent(eventType.FullName!);
            }
        }
        else
        {
            builder.Services.AddSingleton<InMemoryJobState>();
            builder.Services.AddSingleton<IJobs, InMemoryJobs>();
            if (!options.ManageOnly)
            {
                builder.Services.AddHostedService<InMemoryJobScheduler>();
            }
        }

        return builder;
    }

    /// <summary>
    /// A manage-only host registers no runner, so a declaration that only the runner could act on
    /// would be silently inert here. Both cases are rejected now rather than discovered later from
    /// work that never happened.
    /// </summary>
    private static void RejectWhatAManageOnlyHostCannotRun(JobsBuilder jobsBuilder, bool durable)
    {
        var trigger = jobsBuilder.Triggers.FirstOrDefault();
        if (trigger is not null)
        {
            throw new InvalidOperationException(
                $"Event trigger '{trigger.Name}' is declared on a host with ManageOnly set. A trigger fires " +
                "only through the event listener the runner registers, and a manage-only host registers no " +
                "runner, so it would never fire here. No IJobs operation needs it declared: declare it on the " +
                "host that runs jobs and remove it from this one.");
        }

        var schedule = jobsBuilder.Schedules.FirstOrDefault();
        if (!durable && schedule is not null)
        {
            throw new InvalidOperationException(
                $"Job schedule '{schedule.Name}' is declared on a host with ManageOnly set and no persistence " +
                "engine. Without an engine there is no shared ledger for another host to run it from, and this " +
                "host runs nothing, so it would never run. To list and trigger another host's schedules, " +
                "register the engine before AddJobs (conduit.UseSqlite(connectionString) or another); " +
                "otherwise remove ManageOnly.");
        }
    }

    private static bool HasPersistenceEngine(IServiceCollection services)
        => services.Any(descriptor => descriptor.ServiceType == typeof(IConduitConnectionFactory))
            && services.Any(descriptor => descriptor.ServiceType == typeof(ISqlDialect));
}
