using Conduit.Configuration;
using Conduit.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Extension methods for configuring RabbitMQ transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use RabbitMQ as the transport.
    /// Registers the transport as <see cref="IConduitTransport"/> (for event broadcast)
    /// and <see cref="IHostedService"/> (for background receiver lifecycle).
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="connectionString">AMQP connection string (e.g. "amqp://guest:guest@localhost:5672/").</param>
    /// <param name="configure">Optional callback to configure RabbitMQ-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseRabbitMQ(
        this EndpointBuilder builder,
        string connectionString,
        Action<RabbitMQOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new RabbitMQOptions { ConnectionString = connectionString };
        configure?.Invoke(options);

        // Register IServiceCollection for event listener discovery at startup
        builder.Services.AddSingleton<IServiceCollection>(builder.Services);

        // Register options and transport as singletons
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<RabbitMQTransport>();

        // Register as IConduitTransport for event broadcast
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredService<RabbitMQTransport>());

        // Register as IHostedService for background receiver lifecycle
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredService<RabbitMQTransport>());

        // Register keyed transport for endpoint routing
        builder.Services.AddKeyedSingleton<RabbitMQTransport>(
            builder.EndpointName,
            (serviceProvider, _) => serviceProvider.GetRequiredService<RabbitMQTransport>());

        return builder.UseTransport<RabbitMQTransport>();
    }
}
