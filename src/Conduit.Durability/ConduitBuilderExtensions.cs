using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Runtime;
using Conduit.Durability.Runtime.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conduit.Durability;

/// <summary>
/// Extension methods for registering durable messaging on <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderExtensions
{
    /// <summary>
    /// Adds durable messaging (outbox and/or inbox) to the Conduit configuration.
    /// Database is configured once via the builder and shared by all features. Calling it again adds to the same
    /// features: nothing is registered twice (see <see cref="DurabilityBuilder"/>).
    /// </summary>
    public static ConduitBuilder AddDurability(
        this ConduitBuilder builder,
        Action<DurabilityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // The unified table initializer creates the tables of every registered store (outbox, inbox, dead letter
        // queue) on startup. It is registered ahead of every feature's hosted service: the host starts hosted
        // services one at a time in registration order (unless HostOptions.ServicesStartConcurrently is set), and
        // the initializer's StartAsync returns only once its checks and its DDL are done, so the relay and the
        // cleanup never query a table that does not exist yet, nor start before a misconfiguration fails startup.
        builder.Services.AddHostedService<DurabilityTableInitializer>();

        // Every time the stores write or compare (claims, backoff, outcomes, retention, captures) reads this clock, never
        // the ambient one: a host that registers its own (a test's FakeTimeProvider) keeps it.
        builder.Services.TryAddSingleton(TimeProvider.System);

        var durabilityBuilder = new DurabilityBuilder(builder.Services);
        configure(durabilityBuilder);

        // Register the outbox pipeline behavior if outbox was added, once however many AddDurability calls add it
        // (OutboxRegistration registers the behavior type, but ConduitBuilder owns pipeline ordering)
        if (builder.Services.Any(sd => sd.ServiceType == typeof(OutboxCapture))
            && !builder.Options.PipelineTypes.Contains(typeof(OutboxBehavior<,>)))
        {
            builder.AddPipeline(typeof(OutboxBehavior<,>));
        }

        return builder;
    }
}
