using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// Extension methods for configuring ActiveMQ Artemis transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use ActiveMQ Artemis as the transport.
    /// Registers the transport as <see cref="IConduitTransport"/> (for event broadcast)
    /// and <see cref="IHostedService"/> (for background receiver lifecycle).
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="connectionString">AMQP 1.0 connection string (e.g. "amqp://localhost:5672").</param>
    /// <param name="configure">Optional callback to configure ActiveMQ-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseActiveMQ(
        this EndpointBuilder builder,
        string connectionString,
        Action<ActiveMQOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new ActiveMQOptions { ConnectionString = connectionString };
        configure?.Invoke(options);

        // Register options and transport as singletons
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ActiveMQTransport>();

        // Register as IConduitTransport for event broadcast
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredService<ActiveMQTransport>());

        // Register as IHostedService for background receiver lifecycle
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredService<ActiveMQTransport>());

        // Register keyed transport for endpoint routing
        builder.Services.AddKeyedSingleton<ActiveMQTransport>(
            builder.EndpointName,
            (serviceProvider, _) => serviceProvider.GetRequiredService<ActiveMQTransport>());

        return builder.UseTransport<ActiveMQTransport>();
    }
}
