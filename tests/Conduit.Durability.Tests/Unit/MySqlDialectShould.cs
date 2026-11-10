using AwesomeAssertions;
using Conduit.Persistence;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence.MySql;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class MySqlDialectShould
{
    private readonly MySqlDialect dialect = new();

    // Type-name helpers

    [TestMethod]
    public void ExposeMySqlTypeNames()
    {
        // Assert — the MySQL type vocabulary used to compose the durability DDL.
        this.dialect.BigIntAutoIncrementPrimaryKey.Should().Be("BIGINT AUTO_INCREMENT PRIMARY KEY");
        this.dialect.TimestampType.Should().Be("LONGTEXT");
        this.dialect.NullableTimestampType.Should().Be("LONGTEXT");
        this.dialect.TextType.Should().Be("LONGTEXT");
        this.dialect.NullableTextType.Should().Be("LONGTEXT");
        this.dialect.KeyTextType.Should().Be("VARCHAR(255)");
        this.dialect.BlobType.Should().Be("LONGBLOB");
        this.dialect.NullableBlobType.Should().Be("LONGBLOB");
        this.dialect.IntegerType.Should().Be("INT");
        this.dialect.SmallIntType.Should().Be("TINYINT");
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
        var sql = this.dialect.BuildCreateTableIfNotExistsSql("conduit_inbox", "  MessageId VARCHAR(255) PRIMARY KEY");

        // Assert
        sql.Should().Be("CREATE TABLE IF NOT EXISTS conduit_inbox (\n  MessageId VARCHAR(255) PRIMARY KEY\n)");
    }

    [TestMethod]
    public void DeclareTheIndexesInsideTheCreateTable()
    {
        // Act
        var statements = this.dialect.BuildCreateTableIfNotExistsSql(
            "conduit_outbox",
            "    Status TINYINT NOT NULL,\n    NextAttemptAt VARCHAR(255) NOT NULL,\n    ClaimToken VARCHAR(255)",
            [
                new TableIndex("ix_conduit_outbox_status_next_attempt", ["Status", "NextAttemptAt"]),
                new TableIndex("ix_conduit_outbox_claim", ["ClaimToken"]),
            ]);

        // Assert — MySQL has no CREATE INDEX IF NOT EXISTS, so the indexes come with the table, in one statement.
        statements.Should().Equal(
            "CREATE TABLE IF NOT EXISTS conduit_outbox (\n    Status TINYINT NOT NULL,\n    NextAttemptAt VARCHAR(255) NOT NULL,\n    ClaimToken VARCHAR(255),\n    INDEX ix_conduit_outbox_status_next_attempt (Status, NextAttemptAt),\n    INDEX ix_conduit_outbox_claim (ClaimToken)\n)");
    }

    // Insert-or-ignore

    [TestMethod]
    public void BuildInsertOrIgnoreSqlWithInsertIgnore()
    {
        // Act
        var sql = this.dialect.BuildInsertOrIgnoreSql("conduit_inbox", ["MessageId", "ProcessedAt"], "MessageId");

        // Assert
        sql.Should().Be(
            "INSERT IGNORE INTO conduit_inbox (MessageId, ProcessedAt)\nVALUES (@MessageId, @ProcessedAt)");
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

    // Claim builders — derived-table wrapper + FOR UPDATE SKIP LOCKED, parameterized status.

    [TestMethod]
    public void BuildClaimPendingSqlAsTheExactAtomicClaimStatement()
    {
        var sql = this.dialect.BuildClaimPendingSql(OutboxTableSchema.Default);

        sql.Should().Be("""
            UPDATE conduit_outbox
            SET Status = @ClaimingStatus, ClaimedAt = @ClaimedAt, ClaimToken = @ClaimToken
            WHERE Id IN (
                SELECT Id FROM (
                    SELECT Id FROM conduit_outbox
                    WHERE (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                      AND CreatedAt <= @GraceCutoff
                      AND NextAttemptAt <= @Now
                    ORDER BY Id
                    LIMIT @BatchSize
                    FOR UPDATE SKIP LOCKED
                ) AS claimable
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
                SELECT Id FROM (
                    SELECT Id FROM conduit_outbox
                    WHERE MessageId IN ({0})
                      AND (Status = @PendingStatus OR (Status = @ClaimingStatus AND ClaimedAt <= @ClaimCutoff))
                      AND RetryCount = 0
                    ORDER BY Id
                    FOR UPDATE SKIP LOCKED
                ) AS claimable
            )
            """);
    }
}
