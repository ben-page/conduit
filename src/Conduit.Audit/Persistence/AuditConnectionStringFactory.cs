using System.Data.Common;

namespace Conduit.Audit.Persistence;

/// <summary>
/// Creates database connections from a connection string.
/// </summary>
/// <typeparam name="TConnection">The concrete <see cref="DbConnection"/> type to create.</typeparam>
internal sealed class AuditConnectionStringFactory<TConnection>(string connectionString)
    : IAuditConnectionFactory
    where TConnection : DbConnection, new()
{
    public DbConnection CreateConnection()
    {
        var connection = new TConnection { ConnectionString = connectionString };
        return connection;
    }
}
