using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AwesomeAssertions;
using Conduit.Durability.Outbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Jobs;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// The SQL Server claims run whatever isolation level the pooled session was left at. <c>READPAST</c> raises
/// SQL error 650 at Snapshot and Serializable (measured in <see cref="SqlServerPooledSessionShould"/> on
/// SQL Server 2022 with Microsoft.Data.SqlClient 7.1.1), and a
/// host's own transactions at those levels leave a pooled session behind, so the outbox relay, the outbox
/// flush and the jobs sweep each pin READ COMMITTED themselves. Read Uncommitted is not a claim row:
/// the claim already succeeds there, so only the session-level test covers it.
/// </summary>
[TestClass]
public sealed class SqlServerClaimIsolationShould
{
    private static readonly IOptions<JobsOptions> JobsOptionsForTest = Options.Create(
        new JobsOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            ClaimTimeout = TimeSpan.FromMinutes(2),
        });

    [TestInitialize]
    public void TestInitialize()
    {
        if (!SqlServerContainer.IsAvailable)
        {
            Assert.Inconclusive("SqlServerClaimIsolationShould: SQL Server container not available.");
        }
    }

    [TestMethod]
    [DataRow(IsolationLevel.Snapshot)]
    [DataRow(IsolationLevel.Serializable)]
    public async Task ClaimPendingOutboxEntriesOnASessionLeftAtAnotherLevel(IsolationLevel level)
    {
        // Arrange
        var databases = await CreateDatabaseAsync();
        var plain = new SqlServerConduitConnectionFactory(databases.SetupConnectionString);
        var store = new DbConnectionOutboxStore(plain, new SqlServerDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        await SaveEntriesAsync(plain, ["m1", "m2"]);
        var claiming = new DbConnectionOutboxStore(
            new IsolationLevelSettingConnectionFactory(
                new SqlServerConduitConnectionFactory(databases.ClaimConnectionString), level),
            new SqlServerDialect());

        // Act
        var claimed = await claiming.ClaimPendingAsync(10, TimeSpan.Zero, CancellationToken.None);

        // Assert
        claimed.Select(entry => entry.MessageId).Should().BeEquivalentTo("m1", "m2");
        claimed.Should().AllSatisfy(entry => entry.Status.Should().Be(OutboxEntryStatus.Claiming));
    }

    [TestMethod]
    [DataRow(IsolationLevel.Snapshot)]
    [DataRow(IsolationLevel.Serializable)]
    public async Task ClaimOutboxEntriesByMessageIdsOnASessionLeftAtAnotherLevel(IsolationLevel level)
    {
        // Arrange
        var databases = await CreateDatabaseAsync();
        var plain = new SqlServerConduitConnectionFactory(databases.SetupConnectionString);
        var store = new DbConnectionOutboxStore(plain, new SqlServerDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        await SaveEntriesAsync(plain, ["a", "b", "c"]);
        var claiming = new DbConnectionOutboxStore(
            new IsolationLevelSettingConnectionFactory(
                new SqlServerConduitConnectionFactory(databases.ClaimConnectionString), level),
            new SqlServerDialect());

        // Act
        var claimed = await claiming.ClaimByMessageIdsAsync(["a", "c"], CancellationToken.None);

        // Assert
        claimed.Select(entry => entry.MessageId).Should().BeEquivalentTo("a", "c");
    }

    [TestMethod]
    [DataRow(IsolationLevel.Snapshot)]
    [DataRow(IsolationLevel.Serializable)]
    public async Task ClaimADueJobRunOnASessionLeftAtAnotherLevel(IsolationLevel level)
    {
        // Arrange
        var databases = await CreateDatabaseAsync();
        var plain = new DbJobStore(
            new SqlServerConduitConnectionFactory(databases.SetupConnectionString),
            new SqlServerDialect(),
            JobsOptionsForTest);
        await plain.EnsureTablesExistAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await plain.TryMaterializeAsync(NewJobRun("nightly@fixed", now.AddMinutes(-1)), CancellationToken.None);
        var claiming = new DbJobStore(
            new IsolationLevelSettingConnectionFactory(
                new SqlServerConduitConnectionFactory(databases.ClaimConnectionString), level),
            new SqlServerDialect(),
            JobsOptionsForTest);

        // Act
        var claimed = await claiming.ClaimDueAsync("worker", now, 10, CancellationToken.None);

        // Assert
        claimed.Should().ContainSingle().Which.JobKey.Should().Be("nightly@fixed");
    }

    /// <summary>
    /// Characterizes SQL Server and SqlClient, not the fix: after a claim the session reads the level it
    /// carried (5, 4, 1), measured on SQL Server 2022 with Microsoft.Data.SqlClient 7.1.1. The likely reason
    /// (inferred, not measured) is that SqlClient sends a parameterized command through sp_executesql, and a
    /// SET inside it reverts on return. It passes before and after the fix by design; the claim tests above
    /// carry the RED to GREEN proof.
    /// </summary>
    [TestMethod]
    [DataRow(IsolationLevel.Snapshot, 5)]
    [DataRow(IsolationLevel.Serializable, 4)]
    [DataRow(IsolationLevel.ReadUncommitted, 1)]
    public async Task LeaveTheSessionsCarriedLevelInPlaceAfterAClaim(IsolationLevel level, int expectedSessionLevel)
    {
        // Arrange
        var databases = await CreateDatabaseAsync();
        var plain = new SqlServerConduitConnectionFactory(databases.SetupConnectionString);
        var store = new DbConnectionOutboxStore(plain, new SqlServerDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        await SaveEntriesAsync(plain, ["m1"]);
        var claiming = new DbConnectionOutboxStore(
            new IsolationLevelSettingConnectionFactory(
                new SqlServerConduitConnectionFactory(databases.ClaimConnectionString), level),
            new SqlServerDialect());

        // Act: the claim's connection goes back to a pool capped at one session, so this reads that session
        var claimed = await claiming.ClaimPendingAsync(10, TimeSpan.Zero, CancellationToken.None);
        await using var reused = new SqlConnection(databases.ClaimConnectionString);
        await reused.OpenAsync();
        await using var read = reused.CreateCommand();
        read.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
        var sessionLevel = Convert.ToInt32(await read.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        // Assert
        claimed.Should().ContainSingle().Which.MessageId.Should().Be("m1");
        sessionLevel.Should().Be(expectedSessionLevel);
    }

    // CA2100: the database name is a GUID generated in-test, never user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Database name is a GUID generated in-test, not user input.")]
    private static async Task<(string SetupConnectionString, string ClaimConnectionString)> CreateDatabaseAsync()
    {
        var databaseName = $"claim_isolation_{Guid.NewGuid():N}";
        await using (var admin = new SqlConnection(SqlServerContainer.BaseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText =
                $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON;";
            await command.ExecuteNonQueryAsync();
        }

        var setup = new SqlConnectionStringBuilder(SqlServerContainer.BaseConnectionString)
        {
            InitialCatalog = databaseName,
            ApplicationName = $"claim-isolation-setup-{Guid.NewGuid():N}",
        };

        // A pool private to the claim, capped at one physical session, so the level it is left at is certain.
        var claim = new SqlConnectionStringBuilder(SqlServerContainer.BaseConnectionString)
        {
            InitialCatalog = databaseName,
            ApplicationName = $"claim-isolation-claim-{Guid.NewGuid():N}",
            MaxPoolSize = 1,
        };
        return (setup.ConnectionString, claim.ConnectionString);
    }

    private static async Task SaveEntriesAsync(SqlServerConduitConnectionFactory factory, string[] messageIds)
    {
        await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        var store = new DbConnectionOutboxStore(factory, new SqlServerDialect());
        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        var entries = messageIds.Select(messageId => new OutboxEntry
        {
            MessageId = messageId,
            ConversationId = "conv-1",
            Type = "Test.Event",
            Payload = "{}",
            CreatedAt = createdAt,
        }).ToList();
        await store.SaveAsync(entries, transaction, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static JobRun NewJobRun(string jobKey, DateTimeOffset runAt) => new()
    {
        JobKey = jobKey,
        ScheduleName = "nightly",
        Occurrence = runAt,
        MessageType = "Tests.NoopJob",
        Payload = "{}",
        RunAt = runAt,
        Status = JobRunStatus.Pending,
    };
}
