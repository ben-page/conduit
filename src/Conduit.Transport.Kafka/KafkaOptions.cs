using Confluent.Kafka;

namespace Conduit.Transport.Kafka;

public sealed class KafkaOptions
{
    public required string BootstrapServers { get; init; }
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int DefaultMaxDegreeOfParallelism { get; set; } = 10;
    public int MaxRetryCount { get; set; } = 3;
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(1);
    public List<KafkaReceiverPoolOptions> Pools { get; } = [];
    public ProducerConfig? ProducerConfig { get; set; }
    public ConsumerConfig? ConsumerConfig { get; set; }

    public void AddPool(string name, Action<KafkaReceiverPoolOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var pool = new KafkaReceiverPoolOptions { Name = name };
        configure(pool);
        this.Pools.Add(pool);
    }
}
