using System.Data.Common;
using Conduit.Persistence;

namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// Persistence interface for the outbox. Built-in implementation uses
/// raw ADO.NET with <see cref="ISqlDialect"/>. Consumers can provide
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
    /// <summary>
    /// Records a failed send: increments the retry count and releases the claim back to Pending, due again
    /// <paramref name="retryDelay"/> from now. <see cref="ClaimPendingAsync"/> does not claim it before then.
    /// </summary>
    public Task IncrementRetryAsync(long entryId, TimeSpan retryDelay, CancellationToken cancellationToken);
    /// <summary>Marks an entry as permanently failed.</summary>
    public Task MarkFailedAsync(long entryId, CancellationToken cancellationToken);
    /// <summary>Lists failed entries, oldest first, filtered and paged by <paramref name="filter"/>.</summary>
    public Task<FailedOutboxPage> QueryFailedAsync(FailedOutboxFilter filter, CancellationToken cancellationToken);
    /// <summary>
    /// Returns a failed entry to Pending with its retry count reset, due now. Returns <see langword="false"/> when
    /// no failed entry has the id; an entry in any other status is left alone.
    /// </summary>
    public Task<bool> RetryFailedAsync(long entryId, CancellationToken cancellationToken);
    /// <summary>
    /// Returns every failed entry of <paramref name="messageType"/> to Pending with its retry count reset, due now.
    /// Returns the number of entries reset.
    /// </summary>
    public Task<int> RetryFailedAsync(string messageType, CancellationToken cancellationToken);
    /// <summary>Deletes sent entries older than the retention cutoff.</summary>
    public Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);
    /// <summary>Deletes failed (retries-exhausted) entries older than the retention cutoff.</summary>
    public Task DeleteFailedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);
    /// <summary>Creates the outbox table if it does not exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
    /// <summary>Claims entries by MessageId using row-level locking. Returns only Id and MessageId for entries that are still Pending.</summary>
    public Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimByMessageIdsAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken);
}
