namespace Conduit.DeadLetterQueue;

/// <summary>
/// Persistence abstraction for the dead letter queue.
/// Implementations: <c>DbDeadLetterStore</c> (database-backed) or per-transport broker-native stores.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>Capture a failed message into the dead letter store.</summary>
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken);

    /// <summary>Get a single entry by ID. Returns null if not found.</summary>
    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Non-destructive browse with optional filtering and pagination.</summary>
    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken);

    /// <summary>Remove a single entry from the store.</summary>
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Remove multiple entries from the store.</summary>
    public Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Create the backing table/resources if they don't exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
}
