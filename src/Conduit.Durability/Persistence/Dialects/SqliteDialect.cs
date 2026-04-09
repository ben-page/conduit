namespace Conduit.Durability.Persistence.Dialects;

/// <summary>
/// SQLite dialect for durability tables. SQLite is single-writer, so no SKIP LOCKED is needed.
/// </summary>
public sealed class SqliteDialect : IDurabilityDialect
{
    // Outbox

    /// <inheritdoc/>
    public string OutboxCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_outbox (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MessageId TEXT NOT NULL,
            ConversationId TEXT NOT NULL,
            CausationId TEXT,
            Type TEXT NOT NULL,
            Payload TEXT NOT NULL,
            Status INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            SentAt TEXT,
            RetryCount INTEGER NOT NULL DEFAULT 0
        )
        """;

    /// <inheritdoc/>
    public string OutboxInsertEntrySql => """
        INSERT INTO conduit_outbox (MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, RetryCount)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0)
        """;

    /// <inheritdoc/>
    public string OutboxClaimPendingEntriesSql => """
        SELECT Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount
        FROM conduit_outbox
        WHERE Status = 0 AND CreatedAt <= @GraceCutoff
        ORDER BY Id
        LIMIT @BatchSize
        """;

    /// <inheritdoc/>
    public string OutboxMarkSentSql => "UPDATE conduit_outbox SET Status = 1, SentAt = @SentAt WHERE Id = @Id";

    /// <inheritdoc/>
    public string OutboxIncrementRetrySql => "UPDATE conduit_outbox SET RetryCount = RetryCount + 1 WHERE Id = @Id";

    /// <inheritdoc/>
    public string OutboxMarkFailedSql => "UPDATE conduit_outbox SET Status = 2 WHERE Id = @Id";

    /// <inheritdoc/>
    public string OutboxDeleteCompletedSql => "DELETE FROM conduit_outbox WHERE Status IN (1, 2) AND CreatedAt <= @RetentionCutoff";

    /// <inheritdoc/>
    public string OutboxClaimByMessageIdsSql => """
        SELECT Id, MessageId FROM conduit_outbox
        WHERE MessageId IN ({0}) AND Status = 0
        ORDER BY Id
        """;

    // Inbox

    /// <inheritdoc/>
    public string InboxCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_inbox (
            MessageId TEXT PRIMARY KEY,
            ProcessedAt TEXT NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InboxInsertSql => """
        INSERT OR IGNORE INTO conduit_inbox (MessageId, ProcessedAt)
        VALUES (@MessageId, @ProcessedAt)
        """;

    /// <inheritdoc/>
    public string InboxExistsSql => "SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId LIMIT 1";

    /// <inheritdoc/>
    public string InboxDeleteExpiredSql => "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";
}
