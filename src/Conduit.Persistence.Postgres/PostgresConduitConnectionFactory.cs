using System.Data.Common;
using Npgsql;

namespace Conduit.Persistence.Postgres;

/// <summary>
/// Connection-string-based <see cref="IConduitConnectionFactory"/> backed by Npgsql. Opens a fresh
/// <see cref="NpgsqlConnection"/> per call that the caller owns and disposes. It has no visibility
/// into the handler's ambient transaction, so it is marked
/// <see cref="INonTransactionalConnectionFactory"/> and returns <see langword="null"/> from
/// <see cref="GetCurrentTransaction"/>.
/// </summary>
public sealed class PostgresConduitConnectionFactory
    : IConduitConnectionFactory, INonTransactionalConnectionFactory
{
    private readonly string connectionString;

    /// <summary>Initializes a new instance of <see cref="PostgresConduitConnectionFactory"/>.</summary>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    public PostgresConduitConnectionFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(this.connectionString);
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
