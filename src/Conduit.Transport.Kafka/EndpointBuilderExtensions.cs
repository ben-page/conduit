using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Extension methods for configuring Kafka transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use Kafka as the transport.
    /// Registers the transport as <see cref="IConduitTransport"/> (for event broadcast)
    /// and <see cref="IHostedService"/> (for background consumer lifecycle).
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="bootstrapServers">Kafka bootstrap servers (e.g. "localhost:9092").</param>
    /// <param name="configure">Optional callback to configure Kafka-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseKafka(
        this EndpointBuilder builder,
        string bootstrapServers,
        Action<KafkaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);

        var options = new KafkaOptions
        {
            BootstrapServers = bootstrapServers,
            EndpointName = builder.EndpointName,
        };
        configure?.Invoke(options);

        // Register options and transport as singletons
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<KafkaTransport>();

        // Register as IConduitTransport for event broadcast
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredService<KafkaTransport>());

        // Register as IHostedService for background consumer lifecycle
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredService<KafkaTransport>());

        // Register keyed transport for endpoint routing
        builder.Services.AddKeyedSingleton<KafkaTransport>(
            builder.EndpointName,
            (serviceProvider, _) => serviceProvider.GetRequiredService<KafkaTransport>());

        return builder.UseTransport<KafkaTransport>();
    }
}
