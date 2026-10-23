using AwesomeAssertions;
using Conduit.Durability.Persistence.Outbox;
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

    // Create-table-if-not-exists — SQL Server has no inline clause, so it guards with OBJECT_ID.

    [TestMethod]
    public void BuildCreateTableGuardedByObjectIdCheck()
    {
        // Act
        var sql = this.dialect.BuildCreateTableIfNotExistsSql("conduit_inbox", "  MessageId NVARCHAR(450) PRIMARY KEY");

        // Assert
        sql.Should().Be("IF OBJECT_ID(N'conduit_inbox', N'U') IS NULL\nCREATE TABLE conduit_inbox (\n  MessageId NVARCHAR(450) PRIMARY KEY\n)");
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
