namespace Conduit.Runtime;

/// <summary>
/// Configuration for a named receiver pool that isolates a set of message types onto a
/// dedicated consumer with its own concurrency budget. Shared across all broker transports.
/// </summary>
/// <remarks>
/// <para>
/// The pool name is used in broker-specific resource naming (queue / consumer group naming).
/// Message types are assigned by their <c>FullTypeName</c> via <see cref="Handle"/>; any type
/// not assigned to a pool falls back to the transport's default consumer.
/// </para>
/// <para>
/// <see cref="Concurrency"/> is interpreted per broker: it maps to the consumer prefetch count
/// for RabbitMQ and ActiveMQ, the maximum concurrent calls for Azure Service Bus, the polling
/// parallelism bound for Amazon SQS, and the consume parallelism for Kafka.
/// </para>
/// </remarks>
public sealed class ReceiverPoolOptions
{
    private readonly List<string> messageTypes = [];

    /// <summary>
    /// Unique name for this pool. Used in broker-specific resource naming.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Concurrency budget for this pool's consumer. Interpreted per broker (prefetch count for
    /// RabbitMQ/ActiveMQ, max concurrent calls for Azure Service Bus, polling parallelism for
    /// Amazon SQS, consume parallelism for Kafka). Defaults to 1.
    /// </summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// <c>FullTypeName</c> values of the message types routed to this pool's consumer.
    /// </summary>
    public IReadOnlyList<string> MessageTypes => this.messageTypes;

    /// <summary>
    /// Assigns a message type to this pool by its <c>FullTypeName</c>.
    /// </summary>
    /// <param name="messageTypeFullName">The full type name of the message.</param>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.messageTypes.Add(messageTypeFullName);
    }
}
