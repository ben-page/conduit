using System.Data.Common;

namespace Conduit.Persistence;

/// <summary>
/// Provides database connections (and optional ambient transaction access) for the persistence
/// stores shared by Conduit modules (durability, audit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership/disposal contract:</b> <see cref="CreateConnectionAsync"/> returns a freshly
/// opened, independently-disposable <see cref="DbConnection"/>. The <b>caller</b> owns that
/// connection and MUST dispose it (<c>await using</c>). A factory must therefore return a new
/// connection per call — never a shared or pooled instance the caller could dispose out from under
/// another caller. Stores that target an in-memory database typically back every connection with the
/// same shared-cache database so schema and rows remain visible across the separate connections.
/// </para>
/// <para>
/// The exception is the transactional outbox write path, which writes through a caller-supplied
/// <see cref="DbTransaction"/>'s own connection and does not own it. See
/// <see cref="GetCurrentTransaction"/>.
/// </para>
/// </remarks>
public interface IConduitConnectionFactory
{
    /// <summary>
    /// Opens and returns a new database connection. The caller owns the connection and must dispose it.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A freshly opened, independently-disposable connection.</returns>
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the handler's active database transaction for enlisting writes (used by the
    /// transactional outbox). Returns <see langword="null"/> when no transaction is active, in which
    /// case behavior degrades to pass-through.
    /// </summary>
    /// <remarks>
    /// The default implementation returns <see langword="null"/>. Audit never overrides it (audit
    /// writes are not transactional); durability's transaction-aware factories override it to surface
    /// the handler's ambient transaction.
    /// </remarks>
    public DbTransaction? GetCurrentTransaction() => null;
}
