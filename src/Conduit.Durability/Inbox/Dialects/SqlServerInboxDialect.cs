namespace Conduit.Durability.Inbox.Dialects;

/// <summary>
/// SQL Server dialect for the inbox store.
/// </summary>
public sealed class SqlServerInboxDialect : IInboxDialect
{
    /// <inheritdoc/>
    public string CreateTableSql => """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'conduit_inbox')
        CREATE TABLE conduit_inbox (
            MessageId NVARCHAR(64) PRIMARY KEY,
            ProcessedAt DATETIME2 NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InsertSql => """
        INSERT INTO conduit_inbox (MessageId, ProcessedAt)
        SELECT @MessageId, @ProcessedAt
        WHERE NOT EXISTS (SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId)
        """;

    /// <inheritdoc/>
    public string ExistsSql => "SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId";

    /// <inheritdoc/>
    public string DeleteExpiredSql => "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";
}
