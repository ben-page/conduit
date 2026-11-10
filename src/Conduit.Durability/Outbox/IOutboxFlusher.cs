namespace Conduit.Durability.Outbox;

/// <summary>
/// Schedules immediate post-commit delivery of captured outbox messages from the in-memory
/// buffer on the background dispatcher, bypassing the relay's poll cycle. Call after committing
/// the transaction.
/// </summary>
public interface IOutboxFlusher
{
    /// <summary>
    /// Schedules immediate post-commit delivery on the background dispatcher and returns — the
    /// caller never waits; the relay remains the redelivery backstop. No-op (nothing is
    /// scheduled) when nothing was captured during this scope. If the relay has already claimed
    /// the entries by the time the scheduled delivery runs, it completes without redelivering.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default);
}
