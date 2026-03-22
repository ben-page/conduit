namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Configuration for a named receiver pool with a dedicated SQS queue and polling loop.
/// </summary>
public sealed class AmazonSqsReceiverPoolOptions
{
    /// <summary>
    /// The pool name, used in queue naming via TopologyNaming.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Maximum concurrent message processing tasks for this pool.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Maximum number of messages to receive per SQS batch request (1-10).
    /// </summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>
    /// Optional response timeout override for this pool.
    /// When null, the transport-level <see cref="AmazonSqsOptions.ResponseTimeout"/> is used.
    /// </summary>
    public TimeSpan? ResponseTimeout { get; set; }

    /// <summary>
    /// FullTypeName values of message types routed to this pool.
    /// </summary>
    public List<string> MessageTypes { get; } = [];

    /// <summary>
    /// Adds a message type to this pool by FullTypeName.
    /// </summary>
    /// <param name="messageTypeFullName">The full type name of the message.</param>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.MessageTypes.Add(messageTypeFullName);
    }
}
