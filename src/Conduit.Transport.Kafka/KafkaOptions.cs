using Confluent.Kafka;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Configuration for the Kafka transport. Passed to <c>UseKafka()</c> on the endpoint builder.
/// </summary>
public sealed class KafkaOptions
{
    /// <summary>
    /// Kafka bootstrap servers connection string (e.g. "localhost:9092").
    /// </summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default maximum degree of parallelism for message processing. Controls concurrency per consumer.
    /// </summary>
    public int DefaultMaxDegreeOfParallelism { get; set; } = 10;

    /// <summary>
    /// Named consumer pools with separate consumer groups and concurrency settings.
    /// </summary>
    public List<KafkaReceiverPoolOptions> Pools { get; } = [];

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

    /// <summary>
    /// Adds a named consumer pool with the specified configuration.
    /// </summary>
    /// <param name="name">The pool name (used in consumer group naming).</param>
    /// <param name="configure">Callback to configure the pool.</param>
    public void AddPool(string name, Action<KafkaReceiverPoolOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var pool = new KafkaReceiverPoolOptions { Name = name };
        configure(pool);
        this.Pools.Add(pool);
    }
}
