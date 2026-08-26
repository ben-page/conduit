using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Globalization;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// Reads the jobs tables of a <see cref="SharedSqliteDatabase"/> directly, so tests can assert on
/// ledger state — attempt counts, terminal statuses, cursors — without going through a host.
/// </summary>
public static class JobsDatabaseProbe
{
    /// <summary>Builds a store over the shared database.</summary>
    public static IJobStore StoreFor(SharedSqliteDatabase database, JobsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        return new DbJobStore(
            new ProbeConnectionFactory(database),
            new SqliteDialect(),
            Options.Create(options ?? new JobsOptions()));
    }

    /// <summary>
    /// Creates the jobs tables and seeds a schedule's cursor, standing in for an application that has
    /// been running the schedule and then stopped.
    /// </summary>
    public static async Task SeedCursorAsync(
        SharedSqliteDatabase database, string scheduleName, DateTimeOffset lastCompleted)
    {
        var store = StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.ReconcileSchedulesAsync([scheduleName], CancellationToken.None);
        await store.AdvanceCursorAsync(scheduleName, lastCompleted, CancellationToken.None);
    }

    /// <summary>
    /// Waits for the single run row the test expects and returns it. A triggered run's key contains
    /// the event's message id, which a local publish mints, so the test cannot name it in advance.
    /// </summary>
    public static async Task<JobRun> WaitForOnlyRunAsync(
        SharedSqliteDatabase database, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var key = await ReadOnlyJobKeyAsync(database);
            if (key is not null)
            {
                var run = await StoreFor(database).GetRunAsync(key, CancellationToken.None);
                if (run is not null)
                {
                    return run;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No run row was ever written.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<string?> ReadOnlyJobKeyAsync(SharedSqliteDatabase database)
    {
        await using var connection = await database.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT JobKey FROM {JobTableSchema.RunsTable} ORDER BY Id LIMIT 1";
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>Waits until a run reaches a terminal status, or fails the test.</summary>
    public static async Task<JobRun> WaitForTerminalAsync(
        SharedSqliteDatabase database, string jobKey, TimeSpan? timeout = null)
    {
        var store = StoreFor(database);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (true)
        {
            var run = await store.GetRunAsync(jobKey, CancellationToken.None);
            if (run is { Status: JobRunStatus.Failed or JobRunStatus.Cancelled })
            {
                return run;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Run '{jobKey}' did not reach a terminal status (last seen: {run?.Status.ToString() ?? "deleted"}).");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Waits until a run matches a predicate, or fails the test.</summary>
    public static async Task<JobRun> WaitForRunAsync(
        SharedSqliteDatabase database,
        string jobKey,
        Func<JobRun, bool> predicate,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var store = StoreFor(database);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (true)
        {
            var run = await store.GetRunAsync(jobKey, CancellationToken.None);
            if (run is not null && predicate(run))
            {
                return run;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Run '{jobKey}' never matched the expected state.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Waits until the run's claim has been renewed to at least <paramref name="atLeast"/>.</summary>
    public static Task<JobRun> WaitForClaimRenewedAsync(
        SharedSqliteDatabase database, string jobKey, DateTimeOffset atLeast)
        => WaitForRunAsync(database, jobKey, run => run.ClaimedAt >= atLeast);

    /// <summary>Waits until a run's row is gone, which is how success is recorded.</summary>
    public static async Task WaitForDeletionAsync(
        SharedSqliteDatabase database, string jobKey, TimeSpan? timeout = null)
    {
        var store = StoreFor(database);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (await store.GetRunAsync(jobKey, CancellationToken.None) is not null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Run '{jobKey}' was never completed.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Waits until the scopes table holds exactly one row and returns it.</summary>
    public static async Task<JobScope> WaitForOnlyScopeAsync(
        SharedSqliteDatabase database, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var scopes = await StoreFor(database).GetScopesAsync(null, CancellationToken.None);
            if (scopes.Count == 1)
            {
                return scopes[0];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected one scope row; saw {scopes.Count}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Counts the rows currently in the run ledger, whatever their status.</summary>
    public static async Task<int> CountRunsAsync(SharedSqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        await using var connection = await database.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {JobTableSchema.RunsTable}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed class ProbeConnectionFactory : IConduitConnectionFactory
    {
        private readonly SharedSqliteDatabase database;

        public ProbeConnectionFactory(SharedSqliteDatabase database) => this.database = database;

        public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
            => await this.database.OpenAsync();
    }
}
