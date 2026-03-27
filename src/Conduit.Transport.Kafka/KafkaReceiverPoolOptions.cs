namespace Conduit.Transport.Kafka;

/// <summary>
/// Configures a named receiver pool for Kafka concurrency isolation.
/// </summary>
public sealed class KafkaReceiverPoolOptions
{
    /// <summary>The pool name, used in consumer group naming.</summary>
    public required string Name { get; init; }

    /// <summary>Maximum number of messages processed concurrently in this pool. Defaults to 1.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>Full type names of messages assigned to this pool.</summary>
    public List<string> MessageTypes { get; } = [];

    /// <summary>Assigns a message type to this pool by its full type name.</summary>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.MessageTypes.Add(messageTypeFullName);
    }
}
