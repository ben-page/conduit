using Conduit.Runtime;
using Confluent.Kafka;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Configuration for the Kafka transport. Passed to <c>UseKafka()</c> on the endpoint builder.
/// </summary>
/// <remarks>
/// <see cref="BrokerTransportOptions.DefaultConcurrency"/> and
/// <see cref="ReceiverPoolOptions.Concurrency"/> set the number of consumer instances run in a
/// consumer group (the default groups and each pool group, respectively). Kafka balances the
/// shared topic's partitions across those instances, so effective parallelism is bounded by the
/// topic's partition count. Each pool is a dedicated consumer group that exclusively processes its
/// assigned message types.
/// </remarks>
public sealed class KafkaOptions : BrokerTransportOptions
{
    /// <summary>
    /// Kafka bootstrap servers connection string (e.g. "localhost:9092").
    /// </summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The endpoint name this transport is registered under in keyed DI.
    /// Set automatically by <c>UseKafka()</c> from the <c>EndpointBuilder</c> context.
    /// Used by dead letter queue capture to record the source endpoint and by replay to
    /// resolve the correct transport.
    /// </summary>
    public string EndpointName { get; internal set; } = string.Empty;

    /// <summary>
    /// Optional custom producer configuration. Overrides default producer settings derived from
    /// <see cref="BootstrapServers"/>.
    /// </summary>
    public ProducerConfig? ProducerConfig { get; set; }

    /// <summary>
    /// Optional custom consumer configuration. Overrides default consumer settings derived from
    /// <see cref="BootstrapServers"/>.
    /// </summary>
    public ConsumerConfig? ConsumerConfig { get; set; }
}
