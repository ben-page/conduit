using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Extension methods for configuring Amazon SQS (+SNS) transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use Amazon SQS (+SNS) for messaging.
    /// Authentication uses the default AWS credential chain (environment, profile, or IAM role).
    /// Registers the transport as <see cref="IConduitTransport"/> for event broadcast
    /// and <see cref="IHostedService"/> for background receive lifecycle.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="configure">Callback to configure transport options (Region, ServiceUri, etc.).</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseAmazonSqs(
        this EndpointBuilder builder,
        Action<AmazonSqsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AmazonSqsOptions { EndpointName = builder.EndpointName };
        configure(options);

        // Register options
        builder.Services.AddSingleton(options);

        // Register transport as DI-managed singleton
        builder.Services.AddSingleton<AmazonSqsTransport>();

        // Register as IHostedService for background receive lifecycle
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredService<AmazonSqsTransport>());

        // Register as IConduitTransport for event broadcast
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredService<AmazonSqsTransport>());

        // Register keyed transport for endpoint routing
        builder.Services.AddKeyedSingleton<AmazonSqsTransport>(
            builder.EndpointName,
            (serviceProvider, _) => serviceProvider.GetRequiredService<AmazonSqsTransport>());

        return builder.UseTransport<AmazonSqsTransport>();
    }
}
