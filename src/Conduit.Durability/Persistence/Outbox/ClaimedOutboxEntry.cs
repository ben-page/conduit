namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// Lightweight result from claiming outbox entries by MessageId.
/// Contains only the fields needed to correlate with in-memory envelopes
/// and call <see cref="IOutboxStore.MarkSentAsync"/> or
/// <see cref="IOutboxStore.IncrementRetryAsync"/>.
/// </summary>
public sealed class ClaimedOutboxEntry
{
    /// <summary>Database-generated primary key.</summary>
    public required long Id { get; init; }

    /// <summary>Message ID used to correlate with in-memory envelopes.</summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// The token of the claim that returned this entry: resolving the entry passes it back, and resolves it only while
    /// that claim still holds it.
    /// </summary>
    public required string ClaimToken { get; init; }
}
