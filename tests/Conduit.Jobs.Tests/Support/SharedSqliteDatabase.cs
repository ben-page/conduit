using Microsoft.Data.Sqlite;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// One in-memory SQLite database shared by several test hosts, standing in for the one database a
/// cluster's replicas share. The connection held open here is what keeps the database alive;
/// shared-cache mode is what lets independently-opened connections see it, which is required because
/// <c>IConduitConnectionFactory</c> hands out a fresh connection per call.
/// </summary>
public sealed class SharedSqliteDatabase : IAsyncDisposable
{
    private readonly SqliteConnection keepAlive;

    private SharedSqliteDatabase(string connectionString, SqliteConnection keepAlive)
    {
        this.ConnectionString = connectionString;
        this.keepAlive = keepAlive;
    }

    /// <summary>The connection string every host in the test should be configured with.</summary>
    public string ConnectionString { get; }

    /// <summary>Creates a uniquely-named shared in-memory database.</summary>
    public static SharedSqliteDatabase Create()
    {
        var connectionString = $"Data Source=file:jobs-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        return new SharedSqliteDatabase(connectionString, keepAlive);
    }

    /// <summary>Opens a connection for a test to inspect or seed the database directly.</summary>
    public async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(this.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await this.keepAlive.DisposeAsync();
}
