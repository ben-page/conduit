using AwesomeAssertions;
using Conduit.Durability.Persistence.Dialects;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class SqliteDialectShould
{
    private readonly SqliteDialect dialect = new();

    // Outbox

    [TestMethod]
    public void OutboxCreateTableSqlContainsCreateTableIfNotExists()
    {
        this.dialect.OutboxCreateTableSql.Should().Contain("CREATE TABLE IF NOT EXISTS");
    }

    [TestMethod]
    public void OutboxCreateTableSqlContainsRequiredColumns()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxCreateTableSql;

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
    public void OutboxInsertEntrySqlContainsInsertAndRequiredParameters()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxInsertEntrySql;

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
    public void OutboxClaimPendingEntriesSqlContainsSelectAndRequiredParameters()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxClaimPendingEntriesSql;

        // Assert
        sql.Should().Contain("SELECT");
        sql.Should().Contain("WHERE");
        sql.Should().Contain("@GraceCutoff");
        sql.Should().Contain("@BatchSize");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("ORDER BY");
    }

    [TestMethod]
    public void OutboxMarkSentSqlContainsUpdateAndRequiredParameters()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxMarkSentSql;

        // Assert
        sql.Should().Contain("UPDATE");
        sql.Should().Contain("@SentAt");
        sql.Should().Contain("@Id");
    }

    [TestMethod]
    public void OutboxIncrementRetrySqlContainsUpdateAndIdParameter()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxIncrementRetrySql;

        // Assert
        sql.Should().Contain("UPDATE");
        sql.Should().Contain("@Id");
    }

    [TestMethod]
    public void OutboxMarkFailedSqlContainsUpdateAndIdParameter()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxMarkFailedSql;

        // Assert
        sql.Should().Contain("UPDATE");
        sql.Should().Contain("@Id");
    }

    [TestMethod]
    public void OutboxDeleteCompletedSqlContainsDeleteAndRetentionCutoffParameter()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxDeleteCompletedSql;

        // Assert
        sql.Should().Contain("DELETE");
        sql.Should().Contain("@RetentionCutoff");
    }

    [TestMethod]
    public void OutboxClaimByMessageIdsSqlContainsSelectAndMessageIdFilter()
    {
        // Arrange / Act
        var sql = this.dialect.OutboxClaimByMessageIdsSql;

        // Assert
        sql.Should().Contain("SELECT");
        sql.Should().Contain("Id");
        sql.Should().Contain("MessageId");
        sql.Should().Contain("Status = 0");
    }

    // Inbox

    [TestMethod]
    public void InboxCreateTableSqlContainsCreateTableIfNotExists()
    {
        this.dialect.InboxCreateTableSql.Should().Contain("CREATE TABLE IF NOT EXISTS");
    }

    [TestMethod]
    public void InboxCreateTableSqlContainsRequiredColumns()
    {
        // Arrange / Act
        var sql = this.dialect.InboxCreateTableSql;

        // Assert
        sql.Should().Contain("MessageId");
        sql.Should().Contain("ProcessedAt");
    }

    [TestMethod]
    public void InboxInsertSqlUsesInsertOrIgnore()
    {
        // Arrange / Act
        var sql = this.dialect.InboxInsertSql;

        // Assert
        sql.Should().Contain("INSERT OR IGNORE");
        sql.Should().Contain("@MessageId");
        sql.Should().Contain("@ProcessedAt");
    }

    [TestMethod]
    public void InboxExistsSqlSelectsByMessageId()
    {
        // Arrange / Act
        var sql = this.dialect.InboxExistsSql;

        // Assert
        sql.Should().Contain("SELECT");
        sql.Should().Contain("@MessageId");
    }

    [TestMethod]
    public void InboxDeleteExpiredSqlDeletesByCutoff()
    {
        // Arrange / Act
        var sql = this.dialect.InboxDeleteExpiredSql;

        // Assert
        sql.Should().Contain("DELETE");
        sql.Should().Contain("@Cutoff");
    }
}
