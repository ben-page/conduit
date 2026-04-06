using System.Data.Common;

namespace Conduit.Outbox;

/// <summary>
/// Persistence interface for the outbox. Built-in implementation uses
/// raw ADO.NET with <see cref="IOutboxDialect"/>. Consumers can provide
/// their own implementation for non-relational databases.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Saves outbox entries within the handler's database transaction.</summary>
    public Task SaveAsync(IReadOnlyList<OutboxEntry> entries, DbTransaction transaction, CancellationToken cancellationToken);
    /// <summary>Claims pending entries for the relay using row-level locking.</summary>
    public Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, TimeSpan gracePeriod, CancellationToken cancellationToken);
    /// <summary>Marks an entry as successfully sent.</summary>
    public Task MarkSentAsync(long entryId, CancellationToken cancellationToken);
    /// <summary>Increments retry count, keeps status as Pending.</summary>
    public Task IncrementRetryAsync(long entryId, CancellationToken cancellationToken);
    /// <summary>Marks an entry as permanently failed.</summary>
    public Task MarkFailedAsync(long entryId, CancellationToken cancellationToken);
    /// <summary>Deletes sent/failed entries older than the retention cutoff.</summary>
    public Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);
    /// <summary>Creates the outbox table if it does not exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
}
