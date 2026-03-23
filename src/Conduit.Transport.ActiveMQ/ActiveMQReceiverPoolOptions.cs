namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// Configuration for a named receiver pool with its own receiver link and prefetch count.
/// </summary>
public sealed class ActiveMQReceiverPoolOptions
{
    /// <summary>
    /// Unique name for this pool. Used in queue naming.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Prefetch count (AMQP link credit) for this pool's receiver link.
    /// </summary>
    public int PrefetchCount { get; set; } = 1;

    /// <summary>
    /// FullTypeNames of messages routed to this pool's queue.
    /// </summary>
    public List<string> MessageTypes { get; } = [];

    /// <summary>
    /// Adds a message type (by FullTypeName) to this pool.
    /// </summary>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.MessageTypes.Add(messageTypeFullName);
    }
}
