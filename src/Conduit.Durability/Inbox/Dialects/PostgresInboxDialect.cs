namespace Conduit.Durability.Inbox.Dialects;

/// <summary>
/// PostgreSQL dialect for the inbox store.
/// </summary>
public sealed class PostgresInboxDialect : IInboxDialect
{
    /// <inheritdoc/>
    public string CreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_inbox (
            message_id TEXT PRIMARY KEY,
            processed_at TEXT NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InsertSql => """
        INSERT INTO conduit_inbox (message_id, processed_at)
        VALUES (@MessageId, @ProcessedAt)
        ON CONFLICT (message_id) DO NOTHING
        """;

    /// <inheritdoc/>
    public string ExistsSql => "SELECT 1 FROM conduit_inbox WHERE message_id = @MessageId LIMIT 1";

    /// <inheritdoc/>
    public string DeleteExpiredSql => "DELETE FROM conduit_inbox WHERE processed_at < @Cutoff";
}
