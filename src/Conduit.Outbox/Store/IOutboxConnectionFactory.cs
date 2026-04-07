using System.Data.Common;

namespace Conduit.Outbox.Store;

/// <summary>
/// Provides database connections and transaction access for the outbox.
/// Registered as scoped to track per-request transaction state.
/// </summary>
public interface IOutboxConnectionFactory
{
    /// <summary>
    /// Creates a new connection for relay and store operations.
    /// The caller owns the connection and must dispose it.
    /// </summary>
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the handler's active database transaction for enlisting outbox writes.
    /// Returns null if no transaction is active (behavior degrades to pass-through).
    /// </summary>
    public DbTransaction? GetCurrentTransaction();
}
