namespace Conduit.Durability.Inbox.Dialects;

/// <summary>
/// SQLite dialect for the inbox store.
/// </summary>
public sealed class SqliteInboxDialect : IInboxDialect
{
    /// <inheritdoc/>
    public string CreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_inbox (
            MessageId TEXT PRIMARY KEY,
            ProcessedAt TEXT NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InsertSql => """
        INSERT OR IGNORE INTO conduit_inbox (MessageId, ProcessedAt)
        VALUES (@MessageId, @ProcessedAt)
        """;

    /// <inheritdoc/>
    public string ExistsSql => "SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId LIMIT 1";

    /// <inheritdoc/>
    public string DeleteExpiredSql => "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";
}
