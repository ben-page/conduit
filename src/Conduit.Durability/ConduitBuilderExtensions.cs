using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Runtime;
using Conduit.Durability.Runtime.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability;

/// <summary>
/// Extension methods for registering durable messaging on <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderExtensions
{
    /// <summary>
    /// Adds durable messaging (outbox and/or inbox) to the Conduit configuration.
    /// Database is configured once via the builder and shared by all features.
    /// </summary>
    public static ConduitBuilder AddDurability(
        this ConduitBuilder builder,
        Action<DurabilityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var durabilityBuilder = new DurabilityBuilder(builder.Services);
        configure(durabilityBuilder);

        // Register the outbox pipeline behavior if outbox was added
        // (OutboxRegistration registers the behavior type, but ConduitBuilder owns pipeline ordering)
        if (builder.Services.Any(sd => sd.ServiceType == typeof(OutboxCapture)))
        {
            builder.AddPipeline(typeof(OutboxBehavior<,>));
        }

        // Register the unified table initializer — runs on startup and creates tables
        // for all registered durability stores (outbox, inbox, dead letter queue)
        builder.Services.AddHostedService<DurabilityTableInitializer>();

        return builder;
    }
}
