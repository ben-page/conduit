using System.Data.Common;

namespace Conduit.Audit.Persistence;

/// <summary>
/// Creates database connections for the audit store.
/// The caller owns the returned connection and must dispose it.
/// </summary>
public interface IAuditConnectionFactory
{
    /// <summary>
    /// Creates a new database connection.
    /// </summary>
    public DbConnection CreateConnection();
}
