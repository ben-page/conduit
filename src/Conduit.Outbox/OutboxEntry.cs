namespace Conduit.Outbox;

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
}
