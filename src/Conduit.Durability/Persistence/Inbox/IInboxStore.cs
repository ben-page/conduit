namespace Conduit.Durability.Persistence.Inbox;

/// <summary>
/// Stores processed message IDs for deduplication.
/// </summary>
public interface IInboxStore
{
    /// <summary>Checks whether a message has already been processed.</summary>
    public Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically records a message as processed. Uses insert-or-ignore semantics, so it is safe
    /// to call concurrently for the same MessageId. Returns <c>true</c> if this call inserted the
    /// row (the message is newly seen) and <c>false</c> if a row already existed (a duplicate).
    /// This single atomic operation is the deduplication primitive — callers must not pre-check
    /// existence, which would reintroduce a check-then-act race.
    /// </summary>
    public Task<bool> RecordAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a message's record, so the message is admitted again when it next arrives. Called when the
    /// dispatch that recorded it failed: the broker's redelivery, or a dead-letter replay (which carries the
    /// original MessageId), must run rather than be discarded as a duplicate. Removing an id that is not
    /// recorded does nothing.
    /// </summary>
    public Task RemoveAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Deletes inbox entries older than the retention period.</summary>
    public Task DeleteExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);

    /// <summary>Creates the inbox table if it does not exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
}
