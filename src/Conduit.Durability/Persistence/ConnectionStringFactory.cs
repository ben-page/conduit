using System.Data.Common;

namespace Conduit.Durability.Persistence;

internal sealed class ConnectionStringFactory<TConnection>(string connectionString) : IDurabilityConnectionFactory
    where TConnection : DbConnection, new()
{
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new TConnection { ConnectionString = connectionString };
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public DbTransaction? GetCurrentTransaction() => null;
}
