using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// A <see cref="DbJobStore"/> over an isolated in-memory SQLite database with both tables created.
/// The backing <see cref="StubConnectionFactory"/> hands out a fresh connection per call against one
/// shared-cache database, which is what the store's connection-per-method pattern requires.
/// </summary>
public sealed class SqliteJobStoreFixture : IAsyncDisposable
{
    private readonly StubConnectionFactory connectionFactory;

    private SqliteJobStoreFixture(StubConnectionFactory connectionFactory, DbJobStore store)
    {
        this.connectionFactory = connectionFactory;
        this.Store = store;
    }

    /// <summary>The store under test.</summary>
    public DbJobStore Store { get; }

    /// <summary>Creates the fixture with both tables already created.</summary>
    public static async Task<SqliteJobStoreFixture> CreateAsync(JobsOptions? options = null)
    {
        var connectionFactory = new StubConnectionFactory();
        var store = new DbJobStore(
            connectionFactory,
            new SqliteDialect(),
            Options.Create(options ?? new JobsOptions()));

        await store.EnsureTablesExistAsync(CancellationToken.None);
        return new SqliteJobStoreFixture(connectionFactory, store);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await this.connectionFactory.DisposeAsync();
}
