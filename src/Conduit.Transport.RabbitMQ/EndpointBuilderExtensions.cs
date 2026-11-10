using Conduit.Configuration;
using Conduit.Runtime;
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
    /// <param name="connectionString">
    /// AMQP connection string (e.g. "amqp://guest:guest@localhost:5672/"). Overrides the
    /// endpoint's configured <c>ConnectionString</c>, if any. Optional when configuration
    /// supplies one.
    /// </param>
    /// <param name="configure">Optional callback to configure RabbitMQ-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Neither configuration nor code supplied a connection string.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="BrokerTransportOptions.DefaultConcurrency"/> or a pool's <see cref="ReceiverPoolOptions.Concurrency"/>
    /// is outside 1..65535.
    /// </exception>
    public static ConduitEndpointBuilder UseRabbitMQ(
        this ConduitEndpointBuilder builder,
        string? connectionString = null,
        Action<RabbitMQOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RabbitMQOptions(builder.EndpointName);

        // Configuration first, then the explicit argument, then the callback — code always wins.
        ConduitConfigurationBinder.BindTransportOptions(options, builder.Configuration);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            options.ConnectionString = connectionString;
        }

        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                $"RabbitMQ transport for endpoint '{builder.EndpointName}' has no connection string. " +
                $"Set 'Conduit:Endpoints:{builder.EndpointName}:ConnectionString' in configuration " +
                $"or pass it to UseRabbitMQ().");
        }

        options.ThrowIfConcurrencyOutOfRange();

        var endpointName = builder.EndpointName;

        // Options are keyed by endpoint — never non-keyed, or a second endpoint on this
        // transport would silently overwrite the first.
        builder.Services.AddKeyedSingleton(endpointName, options);

        // One transport instance per endpoint. ActivatorUtilities keeps constructor injection
        // automatic for ConduitOptions, IServiceProvider and ILogger<T>; only the options are
        // passed explicitly, resolved through this endpoint's key.
        builder.Services.AddKeyedSingleton<RabbitMQTransport>(
            endpointName,
            (serviceProvider, key) => ActivatorUtilities.CreateInstance<RabbitMQTransport>(
                serviceProvider, serviceProvider.GetRequiredKeyedService<RabbitMQOptions>(key)));

        // One broadcast target and one lifecycle per endpoint, each forwarding to its own instance
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<RabbitMQTransport>(endpointName));
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<RabbitMQTransport>(endpointName));

        return builder.UseTransport<RabbitMQTransport>();
    }
}
