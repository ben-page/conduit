using AwesomeAssertions;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Conduit.Persistence.Postgres;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class PostgresDialectShould
{
    private readonly PostgresDialect dialect = new();

    // Type-name helpers

    [TestMethod]
    public void ExposePostgresTypeNames()
    {
        // Assert — the PostgreSQL type vocabulary used to compose the durability DDL.
        this.dialect.BigIntAutoIncrementPrimaryKey.Should().Be("BIGSERIAL PRIMARY KEY");
        this.dialect.TimestampType.Should().Be("TEXT");
        this.dialect.NullableTimestampType.Should().Be("TEXT");
        this.dialect.TextType.Should().Be("TEXT");
        this.dialect.NullableTextType.Should().Be("TEXT");
        this.dialect.KeyTextType.Should().Be("TEXT");
        this.dialect.BlobType.Should().Be("BYTEA");
        this.dialect.NullableBlobType.Should().Be("BYTEA");
        this.dialect.IntegerType.Should().Be("INT");
        this.dialect.SmallIntType.Should().Be("SMALLINT");
    }

    [TestMethod]
    public void BindTimestampsAsText()
    {
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
            "    Status SMALLINT NOT NULL,\n    NextAttemptAt TEXT NOT NULL,\n    ClaimToken TEXT",
            [
                new TableIndex("ix_conduit_outbox_status_next_attempt", ["Status", "NextAttemptAt"]),
                new TableIndex("ix_conduit_outbox_claim", ["ClaimToken"]),
            ]);

        // Assert — each index is created on its own, so an existing table gains a missing one.
        statements.Should().Equal(
            "CREATE TABLE IF NOT EXISTS conduit_outbox (\n    Status SMALLINT NOT NULL,\n    NextAttemptAt TEXT NOT NULL,\n    ClaimToken TEXT\n)",
            "CREATE INDEX IF NOT EXISTS ix_conduit_outbox_status_next_attempt ON conduit_outbox (Status, NextAttemptAt)",
            "CREATE INDEX IF NOT EXISTS ix_conduit_outbox_claim ON conduit_outbox (ClaimToken)");
    }

    [TestMethod]
    public void RunAReadCommittedStatementAsItIs()
    {
        // Act — Npgsql resets a pooled session, so it carries no level a statement must pin
        var sql = this.dialect.BuildReadCommittedSql("UPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken WHERE Id = @Id");

        // Assert
        sql.Should().Be("UPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken WHERE Id = @Id");
    }

    // Insert-or-ignore

    [TestMethod]
    public void BuildInsertOrIgnoreSqlWithOnConflictDoNothing()
    {
        // Act
        var sql = this.dialect.BuildInsertOrIgnoreSql("conduit_inbox", ["MessageId", "ProcessedAt"], "MessageId");

        // Assert
        sql.Should().Be(
            "INSERT INTO conduit_inbox (MessageId, ProcessedAt)\nVALUES (@MessageId, @ProcessedAt)\nON CONFLICT (MessageId) DO NOTHING");
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
        var sql = this.dialect.BuildExistsSql("conduit_inbox", "MessageId");
        sql.Should().Be("SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId LIMIT 1");
    }

    // Claim builders — FOR UPDATE SKIP LOCKED, parameterized status.

    [TestMethod]
    public void BuildClaimPendingSqlAsTheExactAtomicClaimStatement()
    {
        var sql = this.dialect.BuildClaimPendingSql(OutboxTableSchema.Default);

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
                FOR UPDATE SKIP LOCKED
            )
            """);
    }

    [TestMethod]
    public void BuildClaimByIdsSqlAsTheExactAtomicClaimStatement()
    {
        var sql = this.dialect.BuildClaimByIdsSql(OutboxTableSchema.Default);

        sql.Should().Be("""
            UPDATE conduit_outbox
            SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
            WHERE Id IN (
                SELECT Id FROM conduit_outbox
                WHERE MessageId IN ({0})
                  AND (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                  AND RetryCount = 0
                ORDER BY Id
                FOR UPDATE SKIP LOCKED
            )
            """);
    }
}
