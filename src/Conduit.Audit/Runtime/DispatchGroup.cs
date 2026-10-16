using Conduit.Runtime;

namespace Conduit.Audit.Runtime;

internal sealed class DispatchGroup
{
    private readonly List<MessageObservation> observations = [];
    private readonly string messageId;
    private readonly string conversationId;
    private readonly string? causationId;
    private readonly IReadOnlyDictionary<string, object?>? properties;
    private readonly string serviceName;
    private readonly MessageContext context;
    private readonly int maxPayloadBytes;
    private bool payloadSuppressed;

    internal DispatchGroup(MessageContext context, string serviceName, int maxPayloadBytes)
    {
        this.messageId = context.MessageId;
        this.conversationId = context.ConversationId;
        this.causationId = context.CausationId;
        this.properties = context.Properties.Count > 0
            ? new Dictionary<string, object?>(context.Properties)
            : null;
        this.context = context;
        this.CreatedAt = DateTime.UtcNow;
        this.serviceName = serviceName;
        this.maxPayloadBytes = maxPayloadBytes;
    }

    internal DateTime CreatedAt { get; }

    internal void Add(MessageObservation observation) => this.observations.Add(observation);

    /// <summary>
    /// Omits the captured payload body from the sealed record. Used for types marked via
    /// <c>SuppressPayload&lt;T&gt;()</c> — observations are still recorded, but no body is persisted. Sticky: the
    /// payload is read from the context at seal time, and a suppressed group never reads it.
    /// </summary>
    internal void SuppressPayload() => this.payloadSuppressed = true;

    /// <summary>
    /// Appends a synthetic <see cref="DispatchFailed"/> observation marking this group as evicted
    /// before it emitted a terminal observation. Preserves the partial trail instead of dropping it.
    /// </summary>
    internal void MarkIncomplete(TimeSpan staleTimeout)
    {
        this.observations.Add(new DispatchFailed(
            DurationMs: 0,
            ErrorMessage: $"Dispatch evicted as stale: no terminal observation within {staleTimeout}.",
            ExceptionType: "Conduit.Audit.StaleGroupEviction"));
    }

    /// <summary>
    /// Seals the group. The payload is read from the context the group was created from, at seal time: the wire
    /// dispatch observes <see cref="DispatchInvoked"/> before its switch arm deserializes and captures the message, so
    /// a copy taken at construction would be empty on that path. Every seal path (terminal observation and stale
    /// eviction) therefore keeps the body; a dispatch that failed before capture (unreadable JSON) seals without one.
    /// The body read is capped here, at seal, so both paths store it within the observer's <c>MaxPayloadBytes</c>
    /// (<see cref="AuditPayloadCap"/>).
    /// </summary>
    internal DispatchAuditRecord Seal()
    {
        var invoked = this.observations.OfType<DispatchInvoked>().FirstOrDefault();

        return new DispatchAuditRecord
        {
            MessageId = this.messageId,
            ConversationId = this.conversationId,
            CausationId = this.causationId,
            MessageType = invoked?.MessageType ?? "",
            MessageKind = invoked?.MessageKind ?? "",
            ServiceName = this.serviceName,
            Payload = this.payloadSuppressed ? null : AuditPayloadCap.Apply(this.context.Payload, this.maxPayloadBytes),
            Properties = this.properties,
            Timestamp = this.CreatedAt,
            Observations = this.observations.ToList(),
        };
    }
}
