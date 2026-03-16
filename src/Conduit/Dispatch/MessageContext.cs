namespace Conduit.Dispatch;

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
    /// Creates a new context for a dispatch. Called by the generated mediator.
    /// If a current context exists, inherits <see cref="ConversationId"/> and sets
    /// <see cref="CausationId"/> to the current <see cref="MessageId"/>.
    /// Optionally accepts inbound values from a transport for cross-service propagation.
    /// </summary>
    public static MessageContext StartDispatch(
        string? inboundMessageId = null,
        string? inboundCausationId = null,
        string? inboundConversationId = null)
    {
        var parent = Current;
        var messageId = inboundMessageId ?? Guid.NewGuid().ToString("N");

        var context = new MessageContext(
            messageId: messageId,
            causationId: inboundCausationId ?? parent?.MessageId,
            conversationId: inboundConversationId ?? parent?.ConversationId ?? messageId);

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
