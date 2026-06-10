using System.Data.Common;
using Microsoft.Data.Sqlite;
using Conduit.Persistence;

namespace Conduit.TestUtils;

/// <summary>
/// Test connection factory backed by a shared in-memory SQLite database.
///
/// Each <see cref="CreateConnectionAsync"/> opens a brand-new connection that the caller
/// owns and disposes (honoring the <see cref="IConduitConnectionFactory"/> contract),
/// while every connection targets the same shared-cache in-memory database so schema and
/// rows are visible across them. The long-lived <see cref="Connection"/> keeps the shared
/// in-memory database alive for the duration of the test and is used by tests to assert
/// against the persisted state directly.
/// </summary>
public sealed class StubConnectionFactory : IConduitConnectionFactory, IAsyncDisposable
{
    private readonly string connectionString;
    private DbTransaction? transaction;

    public StubConnectionFactory()
    {
        // A uniquely-named shared-cache in-memory database, isolated per factory instance.
        this.connectionString = $"Data Source=file:conduit-{Guid.NewGuid():N}?mode=memory&cache=shared";
        this.Connection = new SqliteConnection(this.connectionString);
        this.Connection.Open();
    }

    public SqliteConnection Connection { get; }

    public async Task BeginTransactionAsync()
    {
        this.transaction = await this.Connection.BeginTransactionAsync();
    }

    public async Task CommitAsync()
    {
        if (this.transaction != null)
        {
            await this.transaction.CommitAsync();
            this.transaction = null;
        }
    }

    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this.connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public DbTransaction? GetCurrentTransaction() => this.transaction;

    public async ValueTask DisposeAsync()
    {
        if (this.transaction != null)
        {
            await this.transaction.DisposeAsync();
        }

        await this.Connection.DisposeAsync();
    }
}
