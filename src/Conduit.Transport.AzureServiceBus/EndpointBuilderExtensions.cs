using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Extension methods for configuring Azure Service Bus transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use Azure Service Bus for messaging.
    /// Registers the transport as <see cref="IConduitTransport"/> for event broadcast
    /// and <see cref="IHostedService"/> for background receive lifecycle.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="connectionString">The Azure Service Bus connection string.</param>
    /// <param name="configure">Optional callback to configure transport options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseAzureServiceBus(
        this EndpointBuilder builder,
        string connectionString,
        Action<AzureServiceBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new AzureServiceBusOptions
        {
            ConnectionString = connectionString,
            EndpointName = builder.EndpointName,
        };
        configure?.Invoke(options);

        var endpointName = builder.EndpointName;

        builder.Services.AddKeyedSingleton(endpointName, options);

        builder.Services.AddKeyedSingleton<AzureServiceBusTransport>(
            endpointName,
            (serviceProvider, key) => ActivatorUtilities.CreateInstance<AzureServiceBusTransport>(
                serviceProvider, serviceProvider.GetRequiredKeyedService<AzureServiceBusOptions>(key)));

        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<AzureServiceBusTransport>(endpointName));
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<AzureServiceBusTransport>(endpointName));

        return builder.UseTransport<AzureServiceBusTransport>();
    }
}
