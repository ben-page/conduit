using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AwesomeAssertions;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Characterizes SQL Server (and Microsoft.Data.SqlClient's pool), not Conduit: a pooled session keeps the
/// isolation level its last transaction set, and a <c>READPAST</c> claim cannot run at some of those levels
/// (SQL error 650). This is why <c>SqlServerDialect</c>'s claims pin their own level (DMS-6190). If it ever
/// fails, the pin is redundant but harmless: delete this test and keep the pin.
/// </summary>
[TestClass]
public sealed class SqlServerPooledSessionShould
{
    // The unpinned claim as of 6e8247a, so this characterizes SQL Server, not the dialect. It is
    // SqlServerDialect.BuildClaimPendingSql(OutboxTableSchema.Default) copied verbatim at that commit.
    private const string UnpinnedClaimPendingSql = """
        UPDATE conduit_outbox
        SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
        WHERE Id IN (
            SELECT TOP (@BatchSize) Id FROM conduit_outbox WITH (ROWLOCK, UPDLOCK, READPAST)
            WHERE (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
              AND CreatedAt <= @GraceCutoff
            ORDER BY Id
        )
        """;

    // READPAST is only allowed at Read Committed and Repeatable Read; SQL Server raises 650 at Snapshot and
    // Serializable, and accepts it at Read Uncommitted (measured on SQL Server 2022, carried levels 5, 4, 1).
    private const int ReadPastIncompatibleIsolationError = 650;

    private static readonly IsolationLevel[] LevelsThatRaiseError650 = [IsolationLevel.Snapshot, IsolationLevel.Serializable];

    [TestInitialize]
    public void TestInitialize()
    {
        if (!SqlServerEngineShould.IsContainerAvailable)
        {
            Assert.Inconclusive("SqlServerPooledSessionShould: SQL Server container not available.");
        }
    }

    [TestMethod]
    [DataRow(IsolationLevel.Snapshot, 5)]
    [DataRow(IsolationLevel.Serializable, 4)]
    [DataRow(IsolationLevel.ReadUncommitted, 1)]
    public async Task KeepATransactionsIsolationLevelAcrossAPooledReuse(IsolationLevel level, int expectedSessionLevel)
    {
        // Arrange
        var probeConnectionString = await CreateProbeDatabaseAsync();
        var firstClientConnectionId = await RunTransactionAtAsync(probeConnectionString, level);

        // Act
        await using var reused = new SqlConnection(probeConnectionString);
        await reused.OpenAsync();
        var carriedLevel = await ReadSessionIsolationLevelAsync(reused);

        // Assert
        reused.ClientConnectionId.Should().Be(firstClientConnectionId, "the pool holds one physical session");
        carriedLevel.Should().Be(expectedSessionLevel);
    }

    [TestMethod]
    [SuppressMessage("Design", "CA1030:Use events where appropriate",
        Justification = "A test name, not an API: it states that the claim raises SQL error 650.")]
    [DataRow(IsolationLevel.Snapshot)]
    [DataRow(IsolationLevel.Serializable)]
    [DataRow(IsolationLevel.ReadUncommitted)]
    public async Task RaiseError650ForAReadPastClaimAtACarriedLevel(IsolationLevel level)
    {
        // Arrange
        var probeConnectionString = await CreateProbeDatabaseAsync();
        var store = new DbConnectionOutboxStore(
            new SqlServerConduitConnectionFactory(WithoutProbePool(probeConnectionString)),
            new SqlServerDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        var firstClientConnectionId = await RunTransactionAtAsync(probeConnectionString, level);
        await using var reused = new SqlConnection(probeConnectionString);
        await reused.OpenAsync();
        reused.ClientConnectionId.Should().Be(firstClientConnectionId);
        var carriedLevel = await ReadSessionIsolationLevelAsync(reused);
        await using var claim = reused.CreateCommand();
        claim.CommandText = UnpinnedClaimPendingSql;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        claim.Parameters.AddWithValue("@ClaimToken", Guid.NewGuid().ToString("N"));
        claim.Parameters.AddWithValue("@ClaimedAt", now);
        claim.Parameters.AddWithValue("@GraceCutoff", now);
        claim.Parameters.AddWithValue("@ClaimCutoff", now);
        claim.Parameters.AddWithValue("@BatchSize", 10);
        claim.Parameters.AddWithValue("@PendingStatus", 0);
        claim.Parameters.AddWithValue("@ClaimingStatus", 3);

        // Act
        Exception? error = null;
        try
        {
            await claim.ExecuteNonQueryAsync();
        }
        catch (SqlException exception)
        {
            error = exception;
        }

        // Assert
        if (LevelsThatRaiseError650.Contains(level))
        {
            error.Should().BeOfType<SqlException>().Which.Number.Should().Be(ReadPastIncompatibleIsolationError);
        }
        else
        {
            error.Should().BeNull($"a session carrying {level} runs the claim");
        }
    }

    // CA2100: the database name is a GUID generated in-test, never user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Database name is a GUID generated in-test, not user input.")]
    private static async Task<string> CreateProbeDatabaseAsync()
    {
        var databaseName = $"pool_probe_{Guid.NewGuid():N}";
        await using (var admin = new SqlConnection(SqlServerEngineShould.BaseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText =
                $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON;";
            await command.ExecuteNonQueryAsync();
        }

        // A pool private to the test, capped at one physical session, so the reuse is certain.
        var builder = new SqlConnectionStringBuilder(SqlServerEngineShould.BaseConnectionString)
        {
            InitialCatalog = databaseName,
            ApplicationName = $"pool-probe-{Guid.NewGuid():N}",
            MaxPoolSize = 1,
        };
        return builder.ConnectionString;
    }

    private static string WithoutProbePool(string probeConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(probeConnectionString)
        {
            ApplicationName = "pool-probe-setup",
            MaxPoolSize = 100,
        };
        return builder.ConnectionString;
    }

    private static async Task<Guid> RunTransactionAtAsync(string probeConnectionString, IsolationLevel level)
    {
        await using var connection = new SqlConnection(probeConnectionString);
        await connection.OpenAsync();
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(level))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
        }

        return connection.ClientConnectionId;
    }

    private static async Task<int> ReadSessionIsolationLevelAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
