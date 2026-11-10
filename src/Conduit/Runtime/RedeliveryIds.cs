namespace Conduit.Runtime;

/// <summary>
/// The correlation ids a redelivered message carries (<see cref="IConduitTransport.RedeliverAsync"/>): the
/// ones it first arrived with, so a dead-letter replay keeps the original's <see cref="MessageContext"/>
/// lineage instead of starting a conversation of its own. All three are required: a broker transport writes
/// each into a native property, where a blank one would reach the receiver as an id.
/// </summary>
public sealed record RedeliveryIds
{
    /// <summary>Creates the ids of a redelivery.</summary>
    /// <param name="messageId">The original message id.</param>
    /// <param name="causationId">The original causation id.</param>
    /// <param name="conversationId">The original conversation id.</param>
    /// <exception cref="ArgumentException">An id is null, empty or white space.</exception>
    public RedeliveryIds(string messageId, string causationId, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(causationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        this.MessageId = messageId;
        this.CausationId = causationId;
        this.ConversationId = conversationId;
    }

    /// <summary>The original message id.</summary>
    public string MessageId { get; }

    /// <summary>The original causation id.</summary>
    public string CausationId { get; }

    /// <summary>The original conversation id.</summary>
    public string ConversationId { get; }
}
