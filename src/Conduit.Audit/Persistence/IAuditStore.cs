using Conduit.Audit.Runtime;

namespace Conduit.Audit.Persistence;

/// <summary>
/// Persistence interface for audit records. Supports batch insert, single-record retrieval,
/// and time-based purging of expired records.
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Saves a batch of audit records to the store.
    /// </summary>
    public Task SaveBatchAsync(IReadOnlyList<DispatchAuditRecord> records, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves an audit record by its message identifier.
    /// Returns null if no record is found.
    /// </summary>
    public Task<DispatchAuditRecord?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes all audit records with a timestamp before the given cutoff.
    /// </summary>
    public Task PurgeExpiredAsync(DateTime cutoff, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the audit table if it does not already exist.
    /// </summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
}
