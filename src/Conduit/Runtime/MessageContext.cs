using System.Text.Json;

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

    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>();

    /// <summary>
    /// The current message context, or null if no dispatch is active.
    /// </summary>
    public static MessageContext? Current => CurrentContext.Value;

    /// <summary>
    /// The registered observer that receives observation records during dispatch.
    /// Set during host startup by the audit infrastructure.
    /// </summary>
    internal static IConduitObserver? Observer { get; set; }

    /// <summary>Unique ID for this specific message dispatch.</summary>
    public string MessageId { get; }

    /// <summary>MessageId of the message that caused this one. Null for the first message in a chain.</summary>
    public string? CausationId { get; }

    /// <summary>Shared across all messages in a causal chain — the MessageId of the first message.</summary>
    public string ConversationId { get; }

    /// <summary>
    /// User-defined key/value pairs attached to this dispatch via <see cref="DispatchOptions"/>.
    /// Empty when no options were provided.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }

    /// <summary>
    /// The raw JSON payload of the dispatched message. Set by the generated mediator
    /// after serialization, consumed by the audit observer for storage.
    /// </summary>
    internal JsonElement? Payload { get; private set; }

    private MessageContext(string messageId, string? causationId, string conversationId,
        IReadOnlyDictionary<string, object?> properties)
    {
        this.MessageId = messageId;
        this.CausationId = causationId;
        this.ConversationId = conversationId;
        this.Properties = properties;
    }

    /// <summary>
    /// Creates a new context for a local dispatch. If a current context exists,
    /// inherits <see cref="ConversationId"/>, sets <see cref="CausationId"/> to
    /// the current <see cref="MessageId"/>, and inherits <see cref="Properties"/>
    /// unless <paramref name="options"/> provides its own. Called by the generated mediator.
    /// </summary>
    public static MessageContext StartDispatch(DispatchOptions? options = null)
    {
        var parent = Current;
        var messageId = Guid.NewGuid().ToString("N");

        IReadOnlyDictionary<string, object?> properties;
        if (options?.Properties is { Count: > 0 } explicitProps)
        {
            properties = new Dictionary<string, object?>(explicitProps);
        }
        else if (parent?.Properties is { Count: > 0 } parentProps)
        {
            properties = parentProps;
        }
        else
        {
            properties = EmptyProperties;
        }

        var context = new MessageContext(
            messageId: messageId,
            causationId: parent?.MessageId,
            conversationId: parent?.ConversationId ?? messageId,
            properties: properties);

        CurrentContext.Value = context;
        return context;
    }

    /// <summary>
    /// Creates a context from inbound transport values for cross-service propagation.
    /// The three correlation values are required — the sending service provides them via
    /// broker message properties or HTTP headers. Additional <paramref name="properties"/>
    /// are made available to pipeline behaviors via <see cref="Properties"/>.
    /// </summary>
    public static MessageContext StartDispatchFromTransport(
        string messageId,
        string? causationId,
        string conversationId,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var context = new MessageContext(
            messageId: messageId,
            causationId: causationId,
            conversationId: conversationId,
            properties: properties ?? EmptyProperties);

        CurrentContext.Value = context;
        return context;
    }

    /// <summary>
    /// Emits an observation record to the registered observer, if any.
    /// No-ops when no observer is registered or no current context exists.
    /// </summary>
    public static void Observe(MessageObservation observation)
    {
        if (Observer is null)
        {
            return;
        }

        var context = Current;
        if (context is null)
        {
            return;
        }

        Observer.OnObservation(observation, context);
    }

    /// <summary>
    /// Sets the raw JSON payload on this context for audit capture.
    /// Called by the generated mediator after serializing the message.
    /// </summary>
    public void SetPayload(JsonElement payload)
    {
        this.Payload = payload;
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
