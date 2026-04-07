namespace Conduit.Outbox.Store;

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
}
