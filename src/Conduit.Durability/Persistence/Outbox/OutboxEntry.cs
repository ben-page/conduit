namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// Represents a message stored in the outbox table for reliable delivery.
/// </summary>
public sealed class OutboxEntry
{
    /// <summary>Auto-incremented primary key.</summary>
    public long Id { get; init; }
    /// <summary>Message ID from MessageContext.</summary>
    public required string MessageId { get; init; }
    /// <summary>Conversation ID from MessageContext.</summary>
    public required string ConversationId { get; init; }
    /// <summary>Causation ID from MessageContext.</summary>
    public string? CausationId { get; init; }
    /// <summary>Fully-qualified type name of the message.</summary>
    public required string Type { get; init; }
    /// <summary>Serialized JSON payload.</summary>
    public required string Payload { get; init; }
    /// <summary>Current status of the entry.</summary>
    public OutboxEntryStatus Status { get; init; }
    /// <summary>When the entry was created.</summary>
    public DateTime CreatedAt { get; init; }
    /// <summary>When the entry was successfully sent. Null if not yet sent.</summary>
    public DateTime? SentAt { get; init; }
    /// <summary>Number of relay send attempts.</summary>
    public int RetryCount { get; init; }
    /// <summary>
    /// The publishing dispatch's properties that the host lists in
    /// <see cref="Configuration.OutboxOptions.PersistedProperties"/>, as a JSON object; delivery restores them on the
    /// listener's context. Null when none of them was present, and on every entry of a host that lists none.
    /// </summary>
    public string? Properties { get; init; }
    /// <summary>
    /// The token of the claim that returned this entry from <see cref="IOutboxStore.ClaimPendingAsync"/>: the relay
    /// hands it back to <see cref="IOutboxStore.MarkSentAsync"/>, <see cref="IOutboxStore.IncrementRetryAsync"/> or
    /// <see cref="IOutboxStore.MarkFailedAsync"/>, which resolve the entry only while that claim still holds it. Null
    /// on an entry no claim returned (a new entry, a <see cref="IOutboxStore.QueryFailedAsync"/> result).
    /// </summary>
    public string? ClaimToken { get; init; }
}
