using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Conduit.Persistence.Sqlite;

/// <summary>
/// Connection-string-based <see cref="IConduitConnectionFactory"/> backed by
/// Microsoft.Data.Sqlite. Opens a fresh <see cref="SqliteConnection"/> per call that the caller owns
/// and disposes. It has no visibility into the handler's ambient transaction, so it is marked
/// <see cref="INonTransactionalConnectionFactory"/> and returns <see langword="null"/> from
/// <see cref="GetCurrentTransaction"/>.
/// </summary>
public sealed class SqliteConduitConnectionFactory
    : IConduitConnectionFactory, INonTransactionalConnectionFactory
{
    private readonly string connectionString;

    /// <summary>Initializes a new instance of <see cref="SqliteConduitConnectionFactory"/>.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    public SqliteConduitConnectionFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this.connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc/>
    public DbTransaction? GetCurrentTransaction() => null;
}
