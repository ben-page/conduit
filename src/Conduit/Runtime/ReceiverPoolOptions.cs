namespace Conduit.Runtime;

/// <summary>
/// Configuration for a named receiver pool that isolates a set of message types onto a
/// dedicated consumer with its own concurrency budget. Shared across all broker transports.
/// </summary>
/// <remarks>
/// <para>
/// The pool name is used in broker-specific resource naming (queue / consumer group naming).
/// Message types are assigned by their <c>FullTypeName</c> via <see cref="Handle(string)"/>; any type
/// not assigned to a pool falls back to the transport's default consumer.
/// </para>
/// <para>
/// <see cref="Concurrency"/> is interpreted per broker: it maps to the consumer prefetch count
/// for RabbitMQ and ActiveMQ, the maximum concurrent calls for Azure Service Bus, and the polling
/// parallelism bound for Amazon SQS.
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
    /// Amazon SQS). Defaults to 1.
    /// </summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// <c>FullTypeName</c> values of the message types routed to this pool's consumer.
    /// </summary>
    public IReadOnlyList<string> MessageTypes => this.messageTypes;

    /// <summary>
    /// Assigns a message type to this pool by its <c>FullTypeName</c>.
    /// Assigning the same type twice is a no-op.
    /// </summary>
    /// <param name="messageTypeFullName">The full type name of the message.</param>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);

        if (!this.messageTypes.Contains(messageTypeFullName))
        {
            this.messageTypes.Add(messageTypeFullName);
        }
    }

    /// <summary>
    /// Assigns a message type to this pool. Equivalent to passing the type's full name —
    /// a pool assembled from configuration and code holds the union of both.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    public void Handle<TMessage>() => this.Handle(typeof(TMessage));

    /// <summary>
    /// Assigns a message type to this pool. Equivalent to passing the type's full name.
    /// </summary>
    /// <param name="messageType">The message type.</param>
    public void Handle(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (messageType.FullName is not { Length: > 0 } fullName)
        {
            throw new ArgumentException(
                $"Type '{messageType.Name}' has no full name and cannot be assigned to a receiver pool.",
                nameof(messageType));
        }

        this.Handle(fullName);
    }
}
