using AwesomeAssertions;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class SqliteDialectShould
{
    private readonly SqliteDialect dialect = new();

    // Type-name helpers

    [TestMethod]
    public void ExposeSqliteTypeNames()
    {
        // Assert — the SQLite type vocabulary used to compose the durability DDL.
        this.dialect.BigIntAutoIncrementPrimaryKey.Should().Be("INTEGER PRIMARY KEY AUTOINCREMENT");
        this.dialect.TimestampType.Should().Be("TEXT");
        this.dialect.NullableTimestampType.Should().Be("TEXT");
        this.dialect.TextType.Should().Be("TEXT");
        this.dialect.NullableTextType.Should().Be("TEXT");
        this.dialect.KeyTextType.Should().Be("TEXT");
        this.dialect.BlobType.Should().Be("BLOB");
        this.dialect.NullableBlobType.Should().Be("BLOB");
        this.dialect.IntegerType.Should().Be("INTEGER");
        this.dialect.SmallIntType.Should().Be("INTEGER");
    }

    [TestMethod]
    public void BindTimestampsAsStrings()
    {
        // SQLite stores timestamps as ISO 8601 text, so timestamp parameters bind as strings.
        this.dialect.TimestampDbType.Should().Be(System.Data.DbType.String);
    }

    // Create-table-if-not-exists — inline IF NOT EXISTS clause.

    [TestMethod]
    public void BuildCreateTableWithInlineIfNotExists()
    {
        // Act
        var sql = this.dialect.BuildCreateTableIfNotExistsSql("conduit_inbox", "  MessageId TEXT PRIMARY KEY");

        // Assert
        sql.Should().Be("CREATE TABLE IF NOT EXISTS conduit_inbox (\n  MessageId TEXT PRIMARY KEY\n)");
    }

    [TestMethod]
    public void FollowTheTableWithCreateIndexIfNotExists()
    {
        // Act
        var statements = this.dialect.BuildCreateTableIfNotExistsSql(
            "conduit_outbox",
            "    Status INTEGER NOT NULL,\n    NextAttemptAt TEXT NOT NULL,\n    ClaimToken TEXT",
            [
                new TableIndex("ix_conduit_outbox_status_next_attempt", ["Status", "NextAttemptAt"]),
                new TableIndex("ix_conduit_outbox_claim", ["ClaimToken"]),
            ]);

        // Assert — each index is created on its own, so an existing table gains a missing one.
        statements.Should().Equal(
            "CREATE TABLE IF NOT EXISTS conduit_outbox (\n    Status INTEGER NOT NULL,\n    NextAttemptAt TEXT NOT NULL,\n    ClaimToken TEXT\n)",
            "CREATE INDEX IF NOT EXISTS ix_conduit_outbox_status_next_attempt ON conduit_outbox (Status, NextAttemptAt)",
            "CREATE INDEX IF NOT EXISTS ix_conduit_outbox_claim ON conduit_outbox (ClaimToken)");
    }

    [TestMethod]
    public void RunAReadCommittedStatementAsItIs()
    {
        // Act — SQLite has no session isolation level to pin
        var sql = this.dialect.BuildReadCommittedSql("UPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken WHERE Id = @Id");

        // Assert
        sql.Should().Be("UPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken WHERE Id = @Id");
    }

    [TestMethod]
    public void TakeNoErrorForAConcurrentCreate()
    {
        // Arrange — what a bare CREATE TABLE of an existing table raises; SQLite's one writer at a time makes IF NOT EXISTS
        // atomic, so a create-if-absent never does
        var exception = new SqliteException("SQLite Error 1: 'table conduit_inbox already exists'.", 1);

        // Act
        var alreadyExists = this.dialect.IsAlreadyExists(exception);

        // Assert
        alreadyExists.Should().BeFalse();
    }

    // Insert-or-ignore

    [TestMethod]
    public void BuildInsertOrIgnoreSqlForTheInbox()
    {
        // Act
        var sql = this.dialect.BuildInsertOrIgnoreSql("conduit_inbox", ["MessageId", "ProcessedAt"], "MessageId");

        // Assert — exact insert-or-ignore the inbox relies on for its atomic dedup.
        sql.Should().Be(
            "INSERT OR IGNORE INTO conduit_inbox (MessageId, ProcessedAt)\nVALUES (@MessageId, @ProcessedAt)");
    }

    // Pagination

    [TestMethod]
    public void ExposeLimitOffsetPaginationSuffix()
    {
        this.dialect.PaginationSuffix.Should().Be("LIMIT @PageSize OFFSET @Offset");
    }

    // Existence probe

    [TestMethod]
    public void BuildExistsSqlWithLimitOne()
    {
        // Act
        var sql = this.dialect.BuildExistsSql("conduit_inbox", "MessageId");

        // Assert — SQLite caps the probe with LIMIT 1.
        sql.Should().Be("SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId LIMIT 1");
    }

    // Claim builders — asserted byte-for-byte against the known-correct claim SQL. The claim logic
    // (claim token, Pending(0)/Claiming(3) transition, stale-claim grace cutoff, stuck-row recovery)
    // was carefully fixed and must be preserved exactly when templated from the schema.

    [TestMethod]
    public void BuildClaimPendingSqlAsTheExactAtomicClaimStatement()
    {
        // Act
        var sql = this.dialect.BuildClaimPendingSql(OutboxTableSchema.Default);

        // Assert
        sql.Should().Be("""
            UPDATE conduit_outbox
            SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
            WHERE Id IN (
                SELECT Id FROM conduit_outbox
                WHERE (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                  AND CreatedAt <= @GraceCutoff
                  AND NextAttemptAt <= @Now
                ORDER BY Id
                LIMIT @BatchSize
            )
            """);
    }

    [TestMethod]
    public void BuildClaimByIdsSqlAsTheExactAtomicClaimStatement()
    {
        // Act
        var sql = this.dialect.BuildClaimByIdsSql(OutboxTableSchema.Default);

        // Assert — retains the {0} placeholder for the comma-separated MessageId parameter list.
        sql.Should().Be("""
            UPDATE conduit_outbox
            SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
            WHERE Id IN (
                SELECT Id FROM conduit_outbox
                WHERE MessageId IN ({0})
                  AND (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                  AND RetryCount = 0
                ORDER BY Id
            )
            """);
    }
}
