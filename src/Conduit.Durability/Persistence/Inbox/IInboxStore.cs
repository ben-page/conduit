namespace Conduit.Durability.Persistence.Inbox;

/// <summary>
/// Stores processed message IDs for deduplication.
/// </summary>
public interface IInboxStore
{
    /// <summary>Checks whether a message has already been processed.</summary>
    public Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Records a message as processed. Idempotent — duplicate calls are no-ops.</summary>
    public Task RecordAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Deletes inbox entries older than the retention period.</summary>
    public Task DeleteExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken);

    /// <summary>Creates the inbox table if it does not exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
}
