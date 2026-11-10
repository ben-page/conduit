using System.Data.Common;
using Conduit.Durability.Outbox;
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
    /// <summary>
    /// Claims pending entries for the relay using row-level locking, each returned with the claim's
    /// <see cref="OutboxEntry.ClaimToken"/>. A claim older than <see cref="Configuration.OutboxOptions.ClaimTimeout"/>
    /// counts as abandoned, and its entry is claimable again. Cancelled part-way, the call claims nothing: whatever
    /// its claim tagged is handed back (Pending, unclaimed) before the cancellation propagates.
    /// </summary>
    public Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, TimeSpan gracePeriod, CancellationToken cancellationToken);
    /// <summary>
    /// Marks an entry as successfully sent, if the claim <paramref name="claimToken"/> names still holds it. Returns
    /// <see langword="false"/>, changing nothing, when it does not: the claim went stale and another worker took the
    /// entry over, or the entry was already resolved.
    /// </summary>
    public Task<bool> MarkSentAsync(long entryId, string claimToken, CancellationToken cancellationToken);
    /// <summary>
    /// Records a failed send: increments the retry count and releases the claim back to Pending, due again
    /// <paramref name="retryDelay"/> from now. <see cref="ClaimPendingAsync"/> does not claim it before then. Applies
    /// only while the claim <paramref name="claimToken"/> names still holds the entry, and returns whether it did, as
    /// <see cref="MarkSentAsync"/> does.
    /// </summary>
    public Task<bool> IncrementRetryAsync(long entryId, string claimToken, TimeSpan retryDelay, CancellationToken cancellationToken);
    /// <summary>
    /// Marks an entry as permanently failed, recording when (its last attempt, which <see cref="DeleteFailedAsync"/>
    /// counts from). Applies only while the claim <paramref name="claimToken"/> names still holds the entry, and
    /// returns whether it did, as <see cref="MarkSentAsync"/> does.
    /// </summary>
    public Task<bool> MarkFailedAsync(long entryId, string claimToken, CancellationToken cancellationToken);
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
    /// <summary>Deletes the sent entries sent more than <paramref name="retentionPeriod"/> ago.</summary>
    public Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);
    /// <summary>
    /// Deletes the failed (retries-exhausted) entries whose last attempt failed more than
    /// <paramref name="retentionPeriod"/> ago.
    /// </summary>
    public Task DeleteFailedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);
    /// <summary>Creates the outbox table if it does not exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Claims entries by MessageId using row-level locking. Returns only Id, MessageId and the claim's token for
    /// entries that are still Pending. Cancelled part-way, the call claims nothing, as
    /// <see cref="ClaimPendingAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimByMessageIdsAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken);
}
