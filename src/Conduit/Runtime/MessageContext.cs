using System.Text.Json;

namespace Conduit.Runtime;

/// <summary>
/// Tracks message causation through dispatch chains via <see cref="AsyncLocal{T}"/>.
/// Each dispatch gets a unique <see cref="MessageId"/>. A head/origin message self-causates
/// (<see cref="CausationId"/> == <see cref="MessageId"/> == <see cref="ConversationId"/>);
/// <see cref="CausationId"/> is never null. Nested dispatches inherit
/// <see cref="ConversationId"/> and set <see cref="CausationId"/> to the parent's MessageId.
/// Access via <see cref="Current"/>.
/// </summary>
public sealed class MessageContext
{
    private static readonly AsyncLocal<MessageContext?> CurrentContext = new();

    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The current message context, or null if no dispatch is active.
    /// </summary>
    public static MessageContext? Current => CurrentContext.Value;

    /// <summary>
    /// The observer that receives observation records for this dispatch. Resolved from the
    /// dispatching host's DI container (a singleton, so per-host) and attached by the generated
    /// mediator via <see cref="AttachObserver"/>. Carried on the per-flow context — never a
    /// process-global static — so multiple hosts in one process stay isolated.
    /// </summary>
    private IConduitObserver? observer;

    /// <summary>Unique ID for this specific message dispatch.</summary>
    public string MessageId { get; }

    /// <summary>
    /// MessageId of the message that caused this one. A head/origin message self-causates,
    /// so this equals <see cref="MessageId"/> for the first message in a chain. Never null.
    /// </summary>
    public string CausationId { get; }

    /// <summary>Shared across all messages in a causal chain — the MessageId of the first message.</summary>
    public string ConversationId { get; }

    /// <summary>
    /// The MessageId of the message whose delivery this dispatch is. A redelivery of one message — a broker's, or the
    /// outbox relay's after a failed local delivery — carries the same value, while each outbox delivery attempt has its
    /// own <see cref="MessageId"/>; so work a listener must do once per message (a job trigger's run key) keys on this.
    /// Inside the outbox's local delivery (<see cref="StartLocalDelivery"/>) it is the outbox entry's MessageId; on a
    /// wire-inbound dispatch and on every other dispatch it equals <see cref="MessageId"/>. A nested dispatch never
    /// inherits it.
    /// </summary>
    public string DeliveredMessageId { get; }

    /// <summary>
    /// User-defined key/value pairs attached to this dispatch via <see cref="DispatchOptions"/>, a transport (a client's
    /// headers, an enricher's writes) or the outbox's persisted properties. Empty when none were provided. Names are
    /// case-insensitive (<see cref="StringComparer.OrdinalIgnoreCase"/>), whoever built the bag: an HTTP/2 or HTTP/3
    /// request delivers a header name lower-cased, and <c>Properties["TenantCode"]</c> still finds it.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }

    /// <summary>
    /// The raw JSON payload of the dispatched message. Set by the generated mediator
    /// after serialization, consumed by the audit observer for storage.
    /// </summary>
    internal JsonElement? Payload { get; private set; }

    /// <summary>
    /// The dispatch attempt that owns this dispatch, or null: set by the generated mediator
    /// (<see cref="ConduitRuntime.BindDispatchAttempt"/>) only for a dispatch from the current attempt's own scope. Work
    /// that must not outlive an abandoned attempt (a nested dispatch's audit record) goes through its
    /// <see cref="DispatchAttempt.OnKept"/>.
    /// </summary>
    public DispatchAttempt? Attempt { get; internal set; }

    private MessageContext(string messageId, string causationId, string conversationId,
        IReadOnlyDictionary<string, object?> properties, string? deliveredMessageId = null)
    {
        this.MessageId = messageId;
        this.CausationId = causationId;
        this.ConversationId = conversationId;
        this.Properties = CaseInsensitive(properties);
        this.DeliveredMessageId = deliveredMessageId ?? messageId;
    }

    /// <summary>
    /// <paramref name="properties"/> itself when it is already a case-insensitive <see cref="Dictionary{TKey,TValue}"/>
    /// (every bag Conduit builds), else a case-insensitive copy, filled by the indexer so two keys that differ only by
    /// case are one property (the last wins) rather than an exception.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> CaseInsensitive(IReadOnlyDictionary<string, object?> properties)
    {
        if (properties is Dictionary<string, object?> dictionary
            && ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            return properties;
        }

        var copy = new Dictionary<string, object?>(properties.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in properties)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>
    /// Creates a new context for a local dispatch. If a current context exists,
    /// inherits <see cref="ConversationId"/>, sets <see cref="CausationId"/> to
    /// the current <see cref="MessageId"/>, and inherits <see cref="Properties"/>
    /// unless <paramref name="options"/> provides its own. A head/origin dispatch
    /// (no current context) self-causates: <see cref="CausationId"/> equals its own
    /// <see cref="MessageId"/>. Called by the generated mediator.
    /// </summary>
    public static MessageContext StartDispatch(DispatchOptions? options = null)
        => StartChild(options, deliversParent: false);

    /// <summary>
    /// Creates the context for delivering the current context's message to this host's local listeners: a child
    /// dispatch, as <see cref="StartDispatch"/> makes, whose <see cref="DeliveredMessageId"/> is the current context's
    /// <see cref="MessageId"/>. Called by the generated outbox delivery (<c>PublishLocalEnvelopeAsync</c>), whose
    /// caller has restored the outbox entry's context, so every attempt at one entry has its own
    /// <see cref="MessageId"/> but the entry's <see cref="DeliveredMessageId"/>. With no current context it is a head
    /// dispatch.
    /// </summary>
    public static MessageContext StartLocalDelivery() => StartChild(options: null, deliversParent: true);

    private static MessageContext StartChild(DispatchOptions? options, bool deliversParent)
    {
        var parent = Current;
        var messageId = Guid.NewGuid().ToString("N");

        IReadOnlyDictionary<string, object?> properties;
        if (options?.Properties is { Count: > 0 } explicitProps)
        {
            properties = new Dictionary<string, object?>(explicitProps, StringComparer.OrdinalIgnoreCase);
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
            causationId: parent?.MessageId ?? messageId,
            conversationId: parent?.ConversationId ?? messageId,
            properties: properties,
            deliveredMessageId: deliversParent ? parent?.MessageId : null)
        {
            // Nested dispatches inherit the parent's observer; the mediator re-attaches the
            // host's observer for root dispatches via AttachObserver.
            observer = parent?.observer,
        };

        CurrentContext.Value = context;
        return context;
    }

    /// <summary>
    /// Attaches the dispatching host's observer (resolved from its DI container) to this context.
    /// Called by the generated mediator at the start of every dispatch. No-ops when
    /// <paramref name="observer"/> is null so an inherited observer is preserved.
    /// </summary>
    public void AttachObserver(IConduitObserver? observer)
    {
        if (observer is not null)
        {
            this.observer = observer;
        }
    }

    /// <summary>
    /// Creates a context from inbound transport values for cross-service propagation.
    /// <paramref name="messageId"/> and <paramref name="conversationId"/> are required — the
    /// sending service provides them via broker message properties or HTTP headers. When the
    /// wire omits <paramref name="causationId"/> (e.g. a non-Conduit producer), it defaults to
    /// <paramref name="messageId"/> (self-causation) so <see cref="CausationId"/> is never null.
    /// Additional <paramref name="properties"/> are made available to pipeline behaviors via
    /// <see cref="Properties"/>.
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
            causationId: causationId ?? messageId,
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
        var context = Current;
        if (context?.observer is null)
        {
            return;
        }

        context.observer.OnObservation(observation, context);
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
    /// Drops the payload captured on this context. For a host that refuses a request whose captured body must not be
    /// kept (e.g. a filter naming a value the audit trail may not store): the dispatch's record still seals -- failed --
    /// but without a body, because the audit group reads the payload when it seals.
    /// </summary>
    public void ClearPayload()
    {
        this.Payload = null;
    }

    /// <summary>
    /// Whether an observer is attached to this context. The generated mediator and
    /// <see cref="ConduitRuntime.CaptureAuditPayload{T}"/> check it before serializing an audit payload, so a host that
    /// never registered an observer (no <c>AddAudit</c>) pays nothing for capture.
    /// </summary>
    public bool HasObserver => this.observer is not null;

    /// <summary>
    /// Restores the previous context after a dispatch completes.
    /// Called by the generated mediator in a finally block.
    /// </summary>
    public static void RestoreContext(MessageContext? previous)
    {
        CurrentContext.Value = previous;
    }
}
