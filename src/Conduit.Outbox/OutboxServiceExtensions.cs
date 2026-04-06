using Conduit.Configuration;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Conduit.Outbox;

/// <summary>
/// Extension methods for registering the transactional outbox.
/// </summary>
public static class OutboxServiceExtensions
{
    /// <summary>
    /// Adds the transactional outbox to the Conduit configuration.
    /// </summary>
    public static ConduitBuilder AddOutbox(
        this ConduitBuilder builder,
        Action<OutboxBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var outboxBuilder = new OutboxBuilder(builder.Services);
        configure(outboxBuilder);

        if (!outboxBuilder.StoreConfigured)
        {
            throw new InvalidOperationException(
                "Outbox store not configured. Call UseSqlite(), UseSqlServer<T>(), UsePostgres<T>(), UseMySql<T>(), or UseStore<T>().");
        }

        // Register the built-in store if a dialect was configured (not a custom store)
        builder.Services.TryAddScoped<IOutboxStore, DbConnectionOutboxStore>();

        // Register OutboxCapture as both the concrete type and the interceptor interface
        builder.Services.AddScoped<OutboxCapture>();
        builder.Services.AddScoped<IOutboundInterceptor>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());

        // Register the pipeline behavior
        builder.AddPipeline(typeof(OutboxBehavior<,>));

        // Register the relay
        builder.Services.AddHostedService<OutboxRelay>();

        // Register options
        builder.Services.AddSingleton(Options.Create(outboxBuilder.Options));

        return builder;
    }
}
