using Conduit.Configuration;
using Conduit.Dispatch;
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

        var options = new AzureServiceBusOptions { ConnectionString = connectionString };
        configure?.Invoke(options);

        // Register options and IServiceCollection for event listener discovery at startup
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(builder.Services);

        // Register transport as DI-managed singleton
        builder.Services.AddSingleton<AzureServiceBusTransport>();

        // Register as IHostedService for background receive lifecycle
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredService<AzureServiceBusTransport>());

        // Register as IConduitTransport for event broadcast
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredService<AzureServiceBusTransport>());

        // Register keyed transport for endpoint routing
        builder.Services.AddKeyedSingleton<AzureServiceBusTransport>(
            builder.EndpointName,
            (serviceProvider, _) => serviceProvider.GetRequiredService<AzureServiceBusTransport>());

        return builder.UseTransport<AzureServiceBusTransport>();
    }
}
