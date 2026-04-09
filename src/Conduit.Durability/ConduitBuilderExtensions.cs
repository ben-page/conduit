using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Runtime.Outbox;

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

        if (!durabilityBuilder.DatabaseConfigured)
        {
            throw new InvalidOperationException(
                "Durability database not configured. Call UseSqlite(), UseSqlServer<T>(), UsePostgres<T>(), or UseMySql<T>().");
        }

        // Register the outbox pipeline behavior if outbox was added
        // (OutboxRegistration registers the behavior type, but ConduitBuilder owns pipeline ordering)
        if (builder.Services.Any(sd => sd.ServiceType == typeof(OutboxCapture)))
        {
            builder.AddPipeline(typeof(OutboxBehavior<,>));
        }

        return builder;
    }
}
