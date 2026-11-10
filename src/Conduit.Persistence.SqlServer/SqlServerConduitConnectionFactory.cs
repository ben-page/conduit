using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Conduit.Persistence.SqlServer;

/// <summary>
/// Connection-string-based <see cref="IConduitConnectionFactory"/> backed by
/// Microsoft.Data.SqlClient. Opens a fresh <see cref="SqlConnection"/> per call that the caller owns
/// and disposes. It has no visibility into the handler's ambient transaction, so it is marked
/// <see cref="INonTransactionalConnectionFactory"/> and returns <see langword="null"/> from
/// <see cref="GetCurrentTransaction"/>.
/// </summary>
public sealed class SqlServerConduitConnectionFactory
    : IConduitConnectionFactory, INonTransactionalConnectionFactory
{
    private readonly string connectionString;

    /// <summary>Initializes a new instance of <see cref="SqlServerConduitConnectionFactory"/>.</summary>
    /// <param name="connectionString">The SQL Server connection string.</param>
    public SqlServerConduitConnectionFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(this.connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            // The caller never receives a connection that failed to open, so it is disposed here.
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    public DbTransaction? GetCurrentTransaction() => null;
}
