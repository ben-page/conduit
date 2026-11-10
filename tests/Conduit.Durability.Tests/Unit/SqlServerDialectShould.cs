using AwesomeAssertions;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Conduit.Persistence.SqlServer;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class SqlServerDialectShould
{
    private readonly SqlServerDialect dialect = new();

    // Type-name helpers

    [TestMethod]
    public void ExposeSqlServerTypeNames()
    {
        // Assert — the SQL Server type vocabulary used to compose the durability DDL.
        this.dialect.BigIntAutoIncrementPrimaryKey.Should().Be("BIGINT IDENTITY(1,1) PRIMARY KEY");
        this.dialect.TimestampType.Should().Be("NVARCHAR(MAX)");
        this.dialect.NullableTimestampType.Should().Be("NVARCHAR(MAX)");
        this.dialect.TextType.Should().Be("NVARCHAR(MAX)");
        this.dialect.NullableTextType.Should().Be("NVARCHAR(MAX)");
        this.dialect.KeyTextType.Should().Be("NVARCHAR(450)");
        this.dialect.BlobType.Should().Be("VARBINARY(MAX)");
        this.dialect.NullableBlobType.Should().Be("VARBINARY(MAX)");
        this.dialect.IntegerType.Should().Be("INT");
        this.dialect.SmallIntType.Should().Be("TINYINT");
    }

    [TestMethod]
    public void BindTimestampsAsText()
    {
        this.dialect.TimestampDbType.Should().Be(System.Data.DbType.String);
    }

    // Create-table-if-not-exists — SQL Server has no inline clause, so it guards with OBJECT_ID. Each create holds an
    // exclusive application lock for its transaction, so replicas starting together create one at a time (two
    // concurrent CREATE INDEX on one table deadlock, 1205), at READ COMMITTED (a pooled session carrying Snapshot fails
    // a DDL statement that meets another replica's DDL, 3961).
    private const string OneCreateAtATime =
        "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;\nBEGIN TRANSACTION;\n" +
        "EXEC sp_getapplock @Resource = N'conduit_create_if_absent', @LockMode = N'Exclusive', @LockOwner = N'Transaction';\n";

    private const string Commit = ";\nCOMMIT TRANSACTION;";

    [TestMethod]
    public void BuildCreateTableGuardedByObjectIdCheck()
    {
        // Act
        var sql = this.dialect.BuildCreateTableIfNotExistsSql("conduit_inbox", "  MessageId NVARCHAR(450) PRIMARY KEY");

        // Assert
        sql.Should().Be(
            OneCreateAtATime +
            "IF OBJECT_ID(N'conduit_inbox', N'U') IS NULL\nCREATE TABLE conduit_inbox (\n  MessageId NVARCHAR(450) PRIMARY KEY\n)" +
            Commit);
    }

    [TestMethod]
    public void FollowTheTableWithIndexesGuardedBySysIndexesChecks()
    {
        // Act
        var statements = this.dialect.BuildCreateTableIfNotExistsSql(
            "conduit_outbox",
            "    Status TINYINT NOT NULL,\n    NextAttemptAt NVARCHAR(450) NOT NULL,\n    ClaimToken NVARCHAR(450)",
            [
                new TableIndex("ix_conduit_outbox_status_next_attempt", ["Status", "NextAttemptAt"]),
                new TableIndex("ix_conduit_outbox_claim", ["ClaimToken"]),
            ]);

        // Assert — SQL Server has no CREATE INDEX IF NOT EXISTS; each index is guarded on its own, so an existing
        // table gains a missing one.
        statements.Should().Equal(
            OneCreateAtATime +
            "IF OBJECT_ID(N'conduit_outbox', N'U') IS NULL\nCREATE TABLE conduit_outbox (\n    Status TINYINT NOT NULL,\n    NextAttemptAt NVARCHAR(450) NOT NULL,\n    ClaimToken NVARCHAR(450)\n)" +
            Commit,
            OneCreateAtATime +
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_conduit_outbox_status_next_attempt' AND object_id = OBJECT_ID(N'conduit_outbox'))\nCREATE INDEX ix_conduit_outbox_status_next_attempt ON conduit_outbox (Status, NextAttemptAt)" +
            Commit,
            OneCreateAtATime +
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_conduit_outbox_claim' AND object_id = OBJECT_ID(N'conduit_outbox'))\nCREATE INDEX ix_conduit_outbox_claim ON conduit_outbox (ClaimToken)" +
            Commit);
    }

    [TestMethod]
    public void PinAReadCommittedStatementAgainstTheSessionsCarriedLevel()
    {
        // Act — a pooled session can carry Snapshot, where the second of two updates of one row fails (3960) instead of
        // waiting and re-checking its WHERE
        var sql = this.dialect.BuildReadCommittedSql("UPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken WHERE Id = @Id");

        // Assert
        sql.Should().Be(
            "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;\nUPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken WHERE Id = @Id");
    }

    // Insert-or-ignore

    [TestMethod]
    public void BuildInsertOrIgnoreSqlWithWhereNotExists()
    {
        // Act
        var sql = this.dialect.BuildInsertOrIgnoreSql("conduit_inbox", ["MessageId", "ProcessedAt"], "MessageId");

        // Assert
        sql.Should().Be(
            "INSERT INTO conduit_inbox (MessageId, ProcessedAt)\nSELECT @MessageId, @ProcessedAt\nWHERE NOT EXISTS (SELECT 1 FROM conduit_inbox WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = @MessageId)");
    }

    // Pagination

    [TestMethod]
    public void ExposeOffsetFetchPaginationSuffix()
    {
        this.dialect.PaginationSuffix.Should().Be("OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY");
    }

    // Existence probe — no LIMIT (SQL Server has no LIMIT clause).

    [TestMethod]
    public void BuildExistsSqlWithoutLimit()
    {
        var sql = this.dialect.BuildExistsSql("conduit_inbox", "MessageId");
        sql.Should().Be("SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId");
    }

    // Claim builders — TOP (@BatchSize) + ROWLOCK/UPDLOCK/READPAST, parameterized status.

    [TestMethod]
    public void BuildClaimPendingSqlAsTheExactAtomicClaimStatement()
    {
        var sql = this.dialect.BuildClaimPendingSql(OutboxTableSchema.Default);

        sql.Should().Be("""
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
            UPDATE conduit_outbox
            SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
            WHERE Id IN (
                SELECT TOP (@BatchSize) Id FROM conduit_outbox WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                  AND CreatedAt <= @GraceCutoff
                  AND NextAttemptAt <= @Now
                ORDER BY Id
            )
            """);
    }

    [TestMethod]
    public void BuildClaimByIdsSqlAsTheExactAtomicClaimStatement()
    {
        var sql = this.dialect.BuildClaimByIdsSql(OutboxTableSchema.Default);

        sql.Should().Be("""
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
            UPDATE conduit_outbox
            SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
            WHERE Id IN (
                SELECT Id FROM conduit_outbox WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE MessageId IN ({0})
                  AND (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                  AND RetryCount = 0
            )
            """);
    }
}
