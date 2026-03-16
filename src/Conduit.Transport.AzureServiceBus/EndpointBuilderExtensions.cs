using Conduit.Configuration;
using Conduit.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Transport ownership is transferred to DI container via singleton registration.")]
    public static EndpointBuilder UseAzureServiceBus(
        this EndpointBuilder builder,
        string connectionString,
        Action<AzureServiceBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new AzureServiceBusOptions { ConnectionString = connectionString };
        configure?.Invoke(options);

        var loggerFactory = GetLoggerFactory(builder.Services);
        var logger = loggerFactory.CreateLogger<AzureServiceBusTransport>();

        var transport = new AzureServiceBusTransport(options, builder.Services, logger);
        builder.UseTransport(transport);

        // Register as IHostedService via a wrapper that injects IServiceProvider at startup
        builder.Services.AddSingleton<IHostedService>(serviceProvider =>
        {
            transport.SetServiceProvider(serviceProvider);
            return transport;
        });

        // Register as IConduitTransport for event broadcast
        builder.Services.AddSingleton<IConduitTransport>(transport);

        return builder;
    }

    private static ILoggerFactory GetLoggerFactory(IServiceCollection services)
    {
        // Try to find an already-registered ILoggerFactory instance
        var descriptor = services.LastOrDefault(
            d => d.ServiceType == typeof(ILoggerFactory));

        if (descriptor?.ImplementationInstance is ILoggerFactory factory)
        {
            return factory;
        }

        return NullLoggerFactory.Instance;
    }
}
