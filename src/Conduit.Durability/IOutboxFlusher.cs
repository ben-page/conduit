namespace Conduit.Durability;

/// <summary>
/// Sends captured outbox messages immediately from the in-memory buffer,
/// bypassing the relay's poll cycle. Call after committing the transaction.
/// </summary>
public interface IOutboxFlusher
{
    /// <summary>
    /// Claims and sends any outbox messages captured during this scope.
    /// No-op if nothing was captured or if entries were already claimed by the relay.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default);
}
