using System.Data.Common;
using MySqlConnector;

namespace Conduit.Persistence.MySql;

/// <summary>
/// Connection-string-based <see cref="IConduitConnectionFactory"/> backed by MySqlConnector. Opens a
/// fresh <see cref="MySqlConnection"/> per call that the caller owns and disposes. It has no
/// visibility into the handler's ambient transaction, so it is marked
/// <see cref="INonTransactionalConnectionFactory"/> and returns <see langword="null"/> from
/// <see cref="GetCurrentTransaction"/>.
/// </summary>
public sealed class MySqlConduitConnectionFactory
    : IConduitConnectionFactory, INonTransactionalConnectionFactory
{
    private readonly string connectionString;

    /// <summary>Initializes a new instance of <see cref="MySqlConduitConnectionFactory"/>.</summary>
    /// <param name="connectionString">The MySQL connection string.</param>
    public MySqlConduitConnectionFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(this.connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc/>
    public DbTransaction? GetCurrentTransaction() => null;
}
