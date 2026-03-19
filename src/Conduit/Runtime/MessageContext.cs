namespace Conduit.Runtime;

/// <summary>
/// Tracks message causation through dispatch chains via <see cref="AsyncLocal{T}"/>.
/// Each dispatch gets a unique <see cref="MessageId"/>. Nested dispatches inherit
/// <see cref="ConversationId"/> and set <see cref="CausationId"/> to the parent's MessageId.
/// Access via <see cref="Current"/>.
/// </summary>
public sealed class MessageContext
{
    private static readonly AsyncLocal<MessageContext?> CurrentContext = new();

    /// <summary>
    /// The current message context, or null if no dispatch is active.
    /// </summary>
    public static MessageContext? Current => CurrentContext.Value;

    /// <summary>Unique ID for this specific message dispatch.</summary>
    public string MessageId { get; }

    /// <summary>MessageId of the message that caused this one. Null for the first message in a chain.</summary>
    public string? CausationId { get; }

    /// <summary>Shared across all messages in a causal chain — the MessageId of the first message.</summary>
    public string ConversationId { get; }

    private MessageContext(string messageId, string? causationId, string conversationId)
    {
        this.MessageId = messageId;
        this.CausationId = causationId;
        this.ConversationId = conversationId;
    }

    /// <summary>
    /// Creates a new context for a local dispatch. If a current context exists,
    /// inherits <see cref="ConversationId"/> and sets <see cref="CausationId"/>
    /// to the current <see cref="MessageId"/>. Called by the generated mediator.
    /// </summary>
    public static MessageContext StartDispatch()
    {
        var parent = Current;
        var messageId = Guid.NewGuid().ToString("N");

        var context = new MessageContext(
            messageId: messageId,
            causationId: parent?.MessageId,
            conversationId: parent?.ConversationId ?? messageId);

        CurrentContext.Value = context;
        return context;
    }

    /// <summary>
    /// Creates a context from inbound transport values for cross-service propagation.
    /// All three values are required — the sending service provides them via broker
    /// message properties or HTTP headers.
    /// </summary>
    public static MessageContext StartDispatchFromTransport(
        string messageId,
        string? causationId,
        string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var context = new MessageContext(
            messageId: messageId,
            causationId: causationId,
            conversationId: conversationId);

        CurrentContext.Value = context;
        return context;
    }

    /// <summary>
    /// Restores the previous context after a dispatch completes.
    /// Called by the generated mediator in a finally block.
    /// </summary>
    public static void RestoreContext(MessageContext? previous)
    {
        CurrentContext.Value = previous;
    }
}
