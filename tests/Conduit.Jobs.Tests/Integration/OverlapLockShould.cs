using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// <see cref="IJobStore.TryMaterializeWithoutOverlapAsync"/>: the one locked check-and-insert that both
/// the scheduler and trigger-now write a no-overlap schedule's runs through. The server engines' row
/// locking is covered in <c>Conduit.Persistence.IntegrationTests</c>; SQLite proves the protocol.
/// </summary>
[TestClass]
public class OverlapLockShould
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RefuseARunBesideAPendingOneOfTheSameSchedule()
    {
        // Arrange
        await using var database = await LockDatabase.CreateAsync();
        await database.Store.TryMaterializeAsync(OccurrenceRun(Now), CancellationToken.None);

        // Act
        var outcome = await database.Store.TryMaterializeWithoutOverlapAsync(
            ManualRun(Now.AddSeconds(1)), CancellationToken.None);

        // Assert
        outcome.Should().Be(MaterializeOutcome.Overlapping);
        (await database.Store.GetPendingAsync("nightly", CancellationToken.None)).Should().ContainSingle();
    }

    [TestMethod]
    public async Task RefuseARunBesideAClaimedOneOfTheSameSchedule()
    {
        // Arrange
        await using var database = await LockDatabase.CreateAsync();
        await database.Store.TryMaterializeAsync(OccurrenceRun(Now), CancellationToken.None);
        await database.Store.ClaimDueAsync("worker", Now, 10, CancellationToken.None);

        // Act
        var outcome = await database.Store.TryMaterializeWithoutOverlapAsync(
            ManualRun(Now.AddSeconds(1)), CancellationToken.None);

        // Assert
        outcome.Should().Be(MaterializeOutcome.Overlapping);
    }

    [TestMethod]
    public async Task CollapseAReplicaOfTheSameRunRatherThanCallItOverlap()
    {
        // Arrange — every replica materializes the same occurrence at the same moment
        await using var database = await LockDatabase.CreateAsync();
        var first = await database.Store.TryMaterializeWithoutOverlapAsync(OccurrenceRun(Now), CancellationToken.None);

        // Act
        var second = await database.Store.TryMaterializeWithoutOverlapAsync(OccurrenceRun(Now), CancellationToken.None);

        // Assert — the second replica then goes on to claim, as it did before the lock existed
        first.Should().Be(MaterializeOutcome.Materialized);
        second.Should().Be(MaterializeOutcome.AlreadyMaterialized);
    }

    [TestMethod]
    public async Task IgnoreTerminalRunsAndOtherSchedules()
    {
        // Arrange
        await using var database = await LockDatabase.CreateAsync();
        await database.Store.TryMaterializeAsync(OccurrenceRun(Now), CancellationToken.None);
        var claimed = await database.Store.ClaimDueAsync("worker", Now, 10, CancellationToken.None);
        await database.Store.MarkTerminalAsync(
            claimed[0], JobRunStatus.Failed, "gave up", "nightly", claimed[0].Occurrence, Now,
            CancellationToken.None);
        await database.Store.TryMaterializeAsync(
            OccurrenceRun(Now, scheduleName: "hourly"), CancellationToken.None);

        // Act
        var outcome = await database.Store.TryMaterializeWithoutOverlapAsync(
            ManualRun(Now.AddSeconds(1)), CancellationToken.None);

        // Assert — a failed run holds nothing up, and another schedule's run is not this one's
        outcome.Should().Be(MaterializeOutcome.Materialized);
    }

    [TestMethod]
    public async Task CreateAMissingScheduleRowAndStillMaterialize()
    {
        // Arrange — no reconcile, so the row the protocol locks does not exist yet
        await using var database = await LockDatabase.CreateAsync(reconcile: false);

        // Act
        var outcome = await database.Store.TryMaterializeWithoutOverlapAsync(
            ManualRun(Now), CancellationToken.None);

        // Assert
        outcome.Should().Be(MaterializeOutcome.Materialized);
        (await database.Store.GetScheduleAsync("nightly", CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task WaitForTheScheduleLockAndThenSeeTheRunItsHolderCommitted()
    {
        // Arrange — another writer holds the schedule's lock and is about to insert a run
        await using var database = await LockDatabase.CreateAsync();
        await using var holder = await database.Factory.CreateConnectionAsync(CancellationToken.None);
        await using var transaction = await holder.BeginTransactionAsync();
        await ExecuteAsync(
            holder, transaction, "UPDATE conduit_job_schedules SET IsPaused = IsPaused WHERE Name = 'nightly'");

        // Act — on a thread of its own: Microsoft.Data.Sqlite's async methods run synchronously, and
        // its busy retry would otherwise hold this thread until the holder could never commit
        var contender = Task.Run(() => database.Store.TryMaterializeWithoutOverlapAsync(
            ManualRun(Now.AddSeconds(1)), CancellationToken.None));
        await Task.Delay(300);
        var waitedForTheLock = !contender.IsCompleted;

        await ExecuteAsync(holder, transaction, InsertPendingRunSql("nightly@held", Now));
        await transaction.CommitAsync();

        // Assert — it neither ran beside the holder nor read past what the holder committed
        waitedForTheLock.Should().BeTrue();
        (await contender).Should().Be(MaterializeOutcome.Overlapping);
    }

    [TestMethod]
    public async Task KeepAtMostOneRunOutstandingWhenTriggersRaceTheScheduler()
    {
        // Arrange
        await using var database = await LockDatabase.CreateAsync();
        var store = database.Store;

        for (var round = 0; round < 5; round++)
        {
            var occurrence = Now.AddMinutes(round);
            var outstanding = await store.GetPendingAsync("nightly", CancellationToken.None);

            // Act — three replicas materializing one occurrence, three manual triggers, and the
            // completion of the run the last round left outstanding, all at once. Each on a thread of
            // its own, because Microsoft.Data.Sqlite's async methods run synchronously: awaited
            // side by side on one thread they would simply take turns.
            var materializations = Enumerable.Range(0, 3)
                .Select(_ => Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
                    OccurrenceRun(occurrence), CancellationToken.None)))
                .Concat(Enumerable.Range(0, 3).Select(trigger => Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
                    ManualRun(occurrence.AddSeconds(trigger + 1)), CancellationToken.None))))
                .ToList();
            var completion = Task.Run(() => CompleteAsync(store, outstanding));

            var outcomes = await Task.WhenAll(materializations);
            await completion;

            // Assert — every round, whatever the interleaving
            var live = await CountOutstandingAsync(database.Factory);
            live.Should().BeLessThanOrEqualTo(1, $"round {round} must not leave two runs outstanding");
            outcomes.Count(outcome => outcome == MaterializeOutcome.Materialized).Should().Be(live);
        }
    }

    /// <summary>
    /// Claims and completes the runs a previous round left outstanding. The claim is cut off at each
    /// run's own due time, which is before anything the current round writes, so it never takes one.
    /// </summary>
    private static async Task CompleteAsync(DbJobStore store, IReadOnlyList<JobRun> runs)
    {
        foreach (var run in runs)
        {
            var claimed = await store.ClaimDueAsync(
                Guid.NewGuid().ToString("N"), run.RunAt, 10, CancellationToken.None);
            foreach (var claim in claimed)
            {
                await store.CompleteAsync(
                    claim, "nightly", claim.Occurrence, retainRunKey: false, run.RunAt, CancellationToken.None);
            }
        }
    }

    private static async Task<int> CountOutstandingAsync(StubConnectionFactory factory)
    {
        await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned literals.
        command.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT COUNT(*) FROM conduit_job_runs WHERE ScheduleName = 'nightly' AND Status IN ({(int)JobRunStatus.Pending}, {(int)JobRunStatus.Claiming})");
#pragma warning restore CA2100
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static JobRun OccurrenceRun(DateTimeOffset occurrence, string scheduleName = "nightly") => new()
    {
        JobKey = string.Create(CultureInfo.InvariantCulture, $"{scheduleName}@{occurrence.UtcDateTime:O}"),
        ScheduleName = scheduleName,
        Occurrence = occurrence,
        MessageType = "Conduit.Jobs.Tests.Support.NoopJob",
        Payload = "{}",
        RunAt = occurrence,
        Status = JobRunStatus.Pending,
    };

    private static JobRun ManualRun(DateTimeOffset at) => new()
    {
        JobKey = string.Create(CultureInfo.InvariantCulture, $"nightly@manual-{at.UtcDateTime:O}"),
        ScheduleName = "nightly",
        Occurrence = null,
        MessageType = "Conduit.Jobs.Tests.Support.NoopJob",
        Payload = "{}",
        RunAt = at,
        Status = JobRunStatus.Pending,
    };

    private static string InsertPendingRunSql(string jobKey, DateTimeOffset runAt)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"""
            INSERT INTO conduit_job_runs (JobKey, ScheduleName, MessageType, Payload, RunAt, Status)
            VALUES ('{jobKey}', 'nightly', 'Conduit.Jobs.Tests.Support.NoopJob', '{"{}"}', '{runAt.UtcDateTime:O}', {(int)JobRunStatus.Pending})
            """);

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Test-owned literals.
        command.CommandText = sql;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A store over its own shared-cache database, with the factory exposed for raw access.</summary>
    private sealed class LockDatabase : IAsyncDisposable
    {
        private LockDatabase(StubConnectionFactory factory, DbJobStore store)
        {
            this.Factory = factory;
            this.Store = store;
        }

        public StubConnectionFactory Factory { get; }

        public DbJobStore Store { get; }

        public static async Task<LockDatabase> CreateAsync(bool reconcile = true)
        {
            var factory = new StubConnectionFactory();
            var store = new DbJobStore(factory, new SqliteDialect(), Options.Create(new JobsOptions()));
            await store.EnsureTablesExistAsync(CancellationToken.None);
            if (reconcile)
            {
                await store.ReconcileSchedulesAsync(["nightly", "hourly"], CancellationToken.None);
            }

            return new LockDatabase(factory, store);
        }

        public ValueTask DisposeAsync() => this.Factory.DisposeAsync();
    }
}
