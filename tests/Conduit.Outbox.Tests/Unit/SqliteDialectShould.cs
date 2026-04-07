using AwesomeAssertions;
using Conduit.Outbox.Dialects;

namespace Conduit.Outbox.Tests.Unit;

[TestClass]
public class SqliteDialectShould
{
    private readonly SqliteDialect dialect = new();

    [TestMethod]
    public void CreateTableSqlContainsCreateTableIfNotExists()
    {
        this.dialect.CreateTableSql.Should().Contain("CREATE TABLE IF NOT EXISTS");
    }

    [TestMethod]
    public void CreateTableSqlContainsRequiredColumns()
    {
        // Arrange / Act
        var sql = this.dialect.CreateTableSql;

        // Assert
        sql.Should().Contain("Id");
        sql.Should().Contain("MessageId");
        sql.Should().Contain("ConversationId");
        sql.Should().Contain("CausationId");
        sql.Should().Contain("Type");
        sql.Should().Contain("Payload");
        sql.Should().Contain("Status");
        sql.Should().Contain("CreatedAt");
        sql.Should().Contain("SentAt");
        sql.Should().Contain("RetryCount");
    }

    [TestMethod]
    public void InsertEntrySqlContainsInsertAndRequiredParameters()
    {
        // Arrange / Act
        var sql = this.dialect.InsertEntrySql;

        // Assert
        sql.Should().Contain("INSERT INTO");
        sql.Should().Contain("@MessageId");
        sql.Should().Contain("@ConversationId");
        sql.Should().Contain("@CausationId");
        sql.Should().Contain("@Type");
        sql.Should().Contain("@Payload");
        sql.Should().Contain("@CreatedAt");
    }

    [TestMethod]
    public void ClaimPendingEntriesSqlContainsSelectAndRequiredParameters()
    {
        // Arrange / Act
        var sql = this.dialect.ClaimPendingEntriesSql;

        // Assert
        sql.Should().Contain("SELECT");
        sql.Should().Contain("WHERE");
        sql.Should().Contain("@GraceCutoff");
        sql.Should().Contain("@BatchSize");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("ORDER BY");
    }

    [TestMethod]
    public void MarkSentSqlContainsUpdateAndRequiredParameters()
    {
        // Arrange / Act
        var sql = this.dialect.MarkSentSql;

        // Assert
        sql.Should().Contain("UPDATE");
        sql.Should().Contain("@SentAt");
        sql.Should().Contain("@Id");
    }

    [TestMethod]
    public void IncrementRetrySqlContainsUpdateAndIdParameter()
    {
        // Arrange / Act
        var sql = this.dialect.IncrementRetrySql;

        // Assert
        sql.Should().Contain("UPDATE");
        sql.Should().Contain("@Id");
    }

    [TestMethod]
    public void MarkFailedSqlContainsUpdateAndIdParameter()
    {
        // Arrange / Act
        var sql = this.dialect.MarkFailedSql;

        // Assert
        sql.Should().Contain("UPDATE");
        sql.Should().Contain("@Id");
    }

    [TestMethod]
    public void DeleteCompletedSqlContainsDeleteAndRetentionCutoffParameter()
    {
        // Arrange / Act
        var sql = this.dialect.DeleteCompletedSql;

        // Assert
        sql.Should().Contain("DELETE");
        sql.Should().Contain("@RetentionCutoff");
    }

    [TestMethod]
    public void ClaimByMessageIdsSqlContainsSelectAndMessageIdFilter()
    {
        // Arrange
        var dialect = new SqliteDialect();

        // Act
        var sql = dialect.ClaimByMessageIdsSql;

        // Assert
        sql.Should().Contain("SELECT");
        sql.Should().Contain("Id");
        sql.Should().Contain("MessageId");
        sql.Should().Contain("Status = 0");
    }
}
