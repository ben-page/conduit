using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Conduit.Outbox.Tests.Support;

public sealed class StubConnectionFactory : IOutboxConnectionFactory, IAsyncDisposable
{
    private DbTransaction? transaction;

    public StubConnectionFactory()
    {
        this.Connection = new SqliteConnection("Data Source=:memory:");
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

    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
        => Task.FromResult<DbConnection>(this.Connection);

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
