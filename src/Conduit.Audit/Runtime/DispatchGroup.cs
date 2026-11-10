using Conduit.Runtime;

namespace Conduit.Audit.Runtime;

/// <summary>
/// The observations of one dispatch, until it seals. Added to on the dispatching thread and sealed on whichever thread
/// ends it (its terminal observation, or the stale sweep), so every member that touches the observations takes the
/// group's lock: <see cref="Seal"/> returns a consistent snapshot and marks the group sealed, and an
/// <see cref="Add"/> after that is refused (returns false) rather than racing the copy.
/// </summary>
internal sealed class DispatchGroup
{
    private readonly Lock gate = new();
    private readonly List<MessageObservation> observations = [];
    private readonly string messageId;
    private readonly string conversationId;
    private readonly string? causationId;
    private readonly IReadOnlyDictionary<string, object?>? properties;
    private readonly string serviceName;
    private readonly int maxPayloadBytes;
    private bool payloadSuppressed;
    private bool isSealed;

    // createdAt is the observer's clock reading when the dispatch's first observation arrived (UTC).
    internal DispatchGroup(MessageContext context, string serviceName, int maxPayloadBytes, DateTime createdAt)
    {
        this.messageId = context.MessageId;
        this.conversationId = context.ConversationId;
        this.causationId = context.CausationId;
        // Case-insensitive, as the context's bag is.
        this.properties = context.Properties.Count > 0
            ? new Dictionary<string, object?>(context.Properties, StringComparer.OrdinalIgnoreCase)
            : null;
        this.Context = context;
        this.CreatedAt = createdAt;
        this.LastObservedAt = createdAt;
        this.serviceName = serviceName;
        this.maxPayloadBytes = maxPayloadBytes;
    }

    /// <summary>When the dispatch's first observation arrived (UTC): the record's <see cref="DispatchAuditRecord.Timestamp"/>.</summary>
    internal DateTime CreatedAt { get; }

    /// <summary>
    /// When the group last took an observation (UTC, the observer's clock): the time <see cref="SealIfInactive"/> measures
    /// inactivity from. Written and read under the group's lock.
    /// </summary>
    internal DateTime LastObservedAt { get; private set; }

    /// <summary>The context the group was created from, which the observer remembers when the sweep evicts the group.</summary>
    internal MessageContext Context { get; }

    /// <summary>Whether the stale sweep sealed the group (<see cref="SealIfInactive"/>), rather than its terminal observation.</summary>
    internal bool IsEvicted
    {
        get
        {
            lock (this.gate)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>Adds an observation made at <paramref name="observedAt"/>; once the group is sealed, adds nothing and returns false.</summary>
    internal bool Add(MessageObservation observation, DateTime observedAt)
    {
        lock (this.gate)
        {
            if (this.isSealed)
            {
                return false;
            }

            this.observations.Add(observation);
            this.LastObservedAt = observedAt;
            return true;
        }
    }

    /// <summary>
    /// Omits the captured payload body from the sealed record. Used for types marked via
    /// <c>SuppressPayload&lt;T&gt;()</c> — observations are still recorded, but no body is persisted. Sticky: the
    /// payload is read from the context at seal time, and a suppressed group never reads it.
    /// </summary>
    internal void SuppressPayload()
    {
        lock (this.gate)
        {
            this.payloadSuppressed = true;
        }
    }

    /// <summary>
    /// Seals the group as evicted when it has taken no observation for <paramref name="staleTimeout"/> before
    /// <paramref name="now"/>: appends a synthetic <see cref="DispatchFailed"/> marking it evicted before it emitted a
    /// terminal observation, preserving the partial trail instead of dropping it. Checked and sealed under the lock, so
    /// an observation racing the sweep either lands first (and the group is no longer inactive) or is refused.
    /// </summary>
    /// <returns>The sealed record; null when the group observed within the timeout or is already sealed.</returns>
    internal DispatchAuditRecord? SealIfInactive(DateTime now, TimeSpan staleTimeout)
    {
        lock (this.gate)
        {
            if (this.isSealed || now - this.LastObservedAt < staleTimeout)
            {
                return null;
            }

            this.observations.Add(new DispatchFailed(
                DurationMs: 0,
                ErrorMessage: $"Dispatch evicted as stale: no observation for {staleTimeout}.",
                ExceptionType: "Conduit.Audit.StaleGroupEviction"));
            this.IsEvicted = true;
            return this.SealHeld();
        }
    }

    /// <summary>
    /// Seals the group and returns its record; an <see cref="Add"/> after this is refused. The payload is read from the
    /// context the group was created from, at seal time: the wire dispatch observes <see cref="DispatchInvoked"/> before
    /// its switch arm deserializes and captures the message, so a copy taken at construction would be empty on that
    /// path. Every seal path (terminal observation and stale eviction) therefore keeps the body; a dispatch that failed
    /// before capture (unreadable JSON) seals without one. The body read is capped here, at seal, so both paths store it
    /// within the observer's <c>MaxPayloadBytes</c> (<see cref="AuditPayloadCap"/>).
    /// </summary>
    internal DispatchAuditRecord Seal()
    {
        lock (this.gate)
        {
            return this.SealHeld();
        }
    }

    // Called with the lock held.
    private DispatchAuditRecord SealHeld()
    {
        this.isSealed = true;
        var invoked = this.observations.OfType<DispatchInvoked>().FirstOrDefault();

        return new DispatchAuditRecord
        {
            MessageId = this.messageId,
            ConversationId = this.conversationId,
            CausationId = this.causationId,
            MessageType = invoked?.MessageType ?? "",
            MessageKind = invoked?.MessageKind ?? "",
            ServiceName = this.serviceName,
            Payload = this.payloadSuppressed ? null : AuditPayloadCap.Apply(this.Context.Payload, this.maxPayloadBytes),
            Properties = this.properties,
            Timestamp = this.CreatedAt,
            Observations = this.observations.ToList(),
        };
    }
}
