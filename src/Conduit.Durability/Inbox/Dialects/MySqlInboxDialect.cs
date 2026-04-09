namespace Conduit.Durability.Inbox.Dialects;

/// <summary>
/// MySQL dialect for the inbox store.
/// </summary>
public sealed class MySqlInboxDialect : IInboxDialect
{
    /// <inheritdoc/>
    public string CreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_inbox (
            MessageId VARCHAR(64) PRIMARY KEY,
            ProcessedAt DATETIME(6) NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InsertSql => """
        INSERT IGNORE INTO conduit_inbox (MessageId, ProcessedAt)
        VALUES (@MessageId, @ProcessedAt)
        """;

    /// <inheritdoc/>
    public string ExistsSql => "SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId LIMIT 1";

    /// <inheritdoc/>
    public string DeleteExpiredSql => "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";
}
