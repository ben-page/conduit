using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;
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
    /// <param name="bootstrapServers">
    /// Kafka bootstrap servers (e.g. "localhost:9092"). Overrides the endpoint's configured
    /// <c>BootstrapServers</c>, if any. Optional when configuration supplies one.
    /// </param>
    /// <param name="configure">Optional callback to configure Kafka-specific options.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseKafka(
        this EndpointBuilder builder,
        string? bootstrapServers = null,
        Action<KafkaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new KafkaOptions { EndpointName = builder.EndpointName };

        // Configuration first, then the explicit argument, then the callback — code always wins.
        ConduitConfigurationBinder.BindTransportOptions(options, builder.Configuration);
        ApplyDottedKeys(options, builder.Configuration);

        if (!string.IsNullOrWhiteSpace(bootstrapServers))
        {
            options.BootstrapServers = bootstrapServers;
        }

        configure?.Invoke(options);

        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
        {
            throw new InvalidOperationException(
                $"Kafka transport for endpoint '{builder.EndpointName}' has no bootstrap servers. " +
                $"Set 'Conduit:Endpoints:{builder.EndpointName}:BootstrapServers' in configuration " +
                $"or pass it to UseKafka().");
        }

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

    /// <summary>
    /// Applies librdkafka-style dotted keys (e.g. <c>linger.ms</c>) that the configuration binder
    /// ignores, since it matches on C# property names. Both spellings are accepted; a dotted key
    /// would otherwise bind nothing at all, silently.
    /// </summary>
    private static void ApplyDottedKeys(KafkaOptions options, IConfiguration? section)
    {
        if (section is null)
        {
            return;
        }

        ApplyTo(options.ProducerConfig ??= new Confluent.Kafka.ProducerConfig(), section.GetSection("ProducerConfig"));
        ApplyTo(options.ConsumerConfig ??= new Confluent.Kafka.ConsumerConfig(), section.GetSection("ConsumerConfig"));

        static void ApplyTo(Confluent.Kafka.ClientConfig config, IConfiguration configSection)
        {
            foreach (var entry in configSection.GetChildren())
            {
                if (entry.Key.Contains('.', StringComparison.Ordinal)
                    && entry.Value is { Length: > 0 } value)
                {
                    config.Set(entry.Key, value);
                }
            }
        }
    }
}
