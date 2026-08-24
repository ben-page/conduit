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

        var endpointName = builder.EndpointName;

        // Options are keyed by endpoint — never non-keyed, or a second endpoint on this
        // transport would silently overwrite the first.
        builder.Services.AddKeyedSingleton(endpointName, options);

        // One transport instance per endpoint. ActivatorUtilities keeps constructor injection
        // automatic for ConduitOptions, IServiceProvider and ILogger<T>; only the options are
        // passed explicitly, resolved through this endpoint's key.
        builder.Services.AddKeyedSingleton<KafkaTransport>(
            endpointName,
            (serviceProvider, key) => ActivatorUtilities.CreateInstance<KafkaTransport>(
                serviceProvider, serviceProvider.GetRequiredKeyedService<KafkaOptions>(key)));

        // One broadcast target and one lifecycle per endpoint, each forwarding to its own instance
        builder.Services.AddSingleton<IConduitTransport>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<KafkaTransport>(endpointName));
        builder.Services.AddSingleton<IHostedService>(
            serviceProvider => serviceProvider.GetRequiredKeyedService<KafkaTransport>(endpointName));

        return builder.UseTransport<KafkaTransport>();
    }
}
