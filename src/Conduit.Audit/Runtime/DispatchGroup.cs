using Conduit.Runtime;

namespace Conduit.Audit.Runtime;

/// <summary>
/// The observations of one dispatch, until it seals. Added to on the dispatching thread and sealed on the thread that ends
/// it, while the stale sweep may snapshot it from another, so every member that touches the observations takes the
/// group's lock: <see cref="Seal"/> returns a consistent snapshot and marks the group sealed, and an <see cref="Add"/>
/// after that is refused (returns false) rather than racing the copy.
/// </summary>
internal sealed class DispatchGroup
{
    /// <summary>
    /// The <see cref="CustomObservation.Name"/> of the marker that ends a provisional record: the sweep found the dispatch
    /// silent for the stale timeout, and the record has no outcome.
    /// </summary>
    internal const string EvictionMarkerName = "Conduit.Audit.StaleGroupEviction";

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
    /// When the group last took an observation (UTC, the observer's clock): the time <see cref="SnapshotIfInactive"/>
    /// measures inactivity from. Written and read under the group's lock.
    /// </summary>
    internal DateTime LastObservedAt { get; private set; }

    /// <summary>The context the group was created from, which keys the group once the sweep has evicted it.</summary>
    internal MessageContext Context { get; }

    /// <summary>Whether the stale sweep has written a provisional record of the group (<see cref="SnapshotIfInactive"/>).</summary>
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
    /// Writes a provisional record of the group when it has taken no observation for <paramref name="staleTimeout"/>
    /// before <paramref name="now"/> and has not been evicted already: appends a non-terminal
    /// <see cref="CustomObservation"/> marker (<see cref="EvictionMarkerName"/>), sets <see cref="IsEvicted"/>, and hands
    /// <paramref name="write"/> a snapshot ending in that marker. The group is not sealed: it keeps taking observations,
    /// and its terminal one seals the real record. <paramref name="write"/> runs under the group's lock, so the
    /// provisional record is written before any terminal observation can be added, and so before the real record.
    /// </summary>
    /// <returns>True when the provisional record was written; false when the group observed within the timeout, was
    /// evicted before, or is sealed.</returns>
    internal bool SnapshotIfInactive(DateTime now, TimeSpan staleTimeout, Action<DispatchAuditRecord> write)
    {
        lock (this.gate)
        {
            if (this.isSealed || this.IsEvicted || now - this.LastObservedAt < staleTimeout)
            {
                return false;
            }

            this.observations.Add(new CustomObservation(EvictionMarkerName, Data: null) { Timestamp = now });
            this.IsEvicted = true;
            write(this.Snapshot());
            return true;
        }
    }

    /// <summary>
    /// Seals the group and returns its record; an <see cref="Add"/> after this is refused. The payload is read from the
    /// context the group was created from, at seal time: the wire dispatch observes <see cref="DispatchInvoked"/> before
    /// its switch arm deserializes and captures the message, so a copy taken at construction would be empty on that
    /// path. Every record (the real one, and a provisional one at stale eviction) therefore keeps the body; a dispatch
    /// that failed before capture (unreadable JSON) seals without one. The body read is capped here, so every record
    /// stores it within the observer's <c>MaxPayloadBytes</c> (<see cref="AuditPayloadCap"/>).
    /// </summary>
    internal DispatchAuditRecord Seal()
    {
        lock (this.gate)
        {
            this.isSealed = true;
            return this.Snapshot();
        }
    }

    // Called with the lock held.
    private DispatchAuditRecord Snapshot()
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
            Payload = this.payloadSuppressed ? null : AuditPayloadCap.Apply(this.Context.Payload, this.maxPayloadBytes),
            Properties = this.properties,
            Timestamp = this.CreatedAt,
            Observations = this.observations.ToList(),
        };
    }
}
