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
    /// Retrieves the most recently stored audit record with this message identifier that is not provisional (a dispatch
    /// silent past the stale timeout has a provisional record and then its real one, which this returns, whichever was
    /// stored last); a provisional record only when there is no other. Returns null if no record is found.
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
