using System.Data.Common;
using Conduit.Persistence;
using Microsoft.Data.Sqlite;

namespace Conduit.TestUtils;

/// <summary>
/// A connection factory that opens a real SQLite connection per call and tracks how many
/// connections it has created versus how many have been disposed. Backed by a file database
/// so schema and data survive across the separate connections each store method opens.
/// Used to detect connection leaks in the durability stores.
/// </summary>
public sealed class CountingConnectionFactory : IConduitConnectionFactory, IAsyncDisposable
{
    private readonly string connectionString;
    private int createdCount;
    private int disposedCount;

    public CountingConnectionFactory()
    {
        // A private, per-instance file database keeps the data alive across the multiple
        // short-lived connections the stores open while remaining isolated per test.
        this.DatabasePath = Path.Combine(Path.GetTempPath(), $"conduit-leak-{Guid.NewGuid():N}.db");
        this.connectionString = $"Data Source={this.DatabasePath}";
    }

    public string DatabasePath { get; }

    public int CreatedCount => Volatile.Read(ref this.createdCount);

    public int DisposedCount => Volatile.Read(ref this.disposedCount);

    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new TrackedSqliteConnection(
            this.connectionString,
            () => Interlocked.Increment(ref this.disposedCount));
        await connection.OpenAsync(cancellationToken);
        Interlocked.Increment(ref this.createdCount);
        return connection;
    }

    public DbTransaction? GetCurrentTransaction() => null;

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(this.DatabasePath))
        {
            File.Delete(this.DatabasePath);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// SQLite connection that invokes a callback exactly once when it is disposed
    /// (whether synchronously or asynchronously), so the factory can count disposals.
    /// </summary>
    private sealed class TrackedSqliteConnection(string connectionString, Action onDisposed)
        : SqliteConnection(connectionString)
    {
        private int disposed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                onDisposed();
            }

            base.Dispose(disposing);
        }
    }
}
