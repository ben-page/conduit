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
    /// <param name="connectionString">
    /// AMQP 1.0 connection string (e.g. "amqp://localhost:5672"). Overrides the endpoint's
    /// configured <c>ConnectionString</c>, if any. Optional when configuration supplies one.
    /// </param>
    /// <param name="configure">Optional callback to configure ActiveMQ-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseActiveMQ(
        this EndpointBuilder builder,
        string? connectionString = null,
        Action<ActiveMQOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ActiveMQOptions { EndpointName = builder.EndpointName };

        ConduitConfigurationBinder.BindTransportOptions(options, builder.Configuration);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            options.ConnectionString = connectionString;
        }

        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                $"ActiveMQ transport for endpoint '{builder.EndpointName}' has no connection string. " +
                $"Set 'Conduit:Endpoints:{builder.EndpointName}:ConnectionString' in configuration " +
                $"or pass it to UseActiveMQ().");
        }

        var endpointName = builder.EndpointName;

        builder.Services.AddKeyedSingleton(endpointName, options);

        builder.Services.AddKeyedSingleton<ActiveMQTransport>(
            endpointName,
            (serviceProvider, key) => ActivatorUtilities.CreateInstance<ActiveMQTransport>(
                serviceProvider, serviceProvider.GetRequiredKeyedService<ActiveMQOptions>(key)));

        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<ActiveMQTransport>(endpointName));
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<ActiveMQTransport>(endpointName));

        return builder.UseTransport<ActiveMQTransport>();
    }
}
