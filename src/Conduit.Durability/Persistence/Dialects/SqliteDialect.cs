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

    // Dead Letter Queue

    /// <inheritdoc/>
    public string DeadLetterCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_dead_letters (
            Id TEXT NOT NULL PRIMARY KEY,
            MessageType TEXT NOT NULL,
            Body BLOB NOT NULL,
            SourceEndpoint TEXT NOT NULL,
            ExceptionType TEXT,
            ExceptionMessage TEXT,
            MessageId TEXT,
            CausationId TEXT,
            ConversationId TEXT,
            AttemptCount INTEGER NOT NULL DEFAULT 1,
            DeadLetteredAt TEXT NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string DeadLetterInsertSql => """
        INSERT INTO conduit_dead_letters (Id, MessageType, Body, SourceEndpoint, ExceptionType, ExceptionMessage, MessageId, CausationId, ConversationId, AttemptCount, DeadLetteredAt)
        VALUES (@Id, @MessageType, @Body, @SourceEndpoint, @ExceptionType, @ExceptionMessage, @MessageId, @CausationId, @ConversationId, @AttemptCount, @DeadLetteredAt)
        """;

    /// <inheritdoc/>
    public string DeadLetterSelectByIdSql => """
        SELECT Id, MessageType, Body, SourceEndpoint, ExceptionType, ExceptionMessage, MessageId, CausationId, ConversationId, AttemptCount, DeadLetteredAt
        FROM conduit_dead_letters WHERE Id = @Id
        """;

    /// <inheritdoc/>
    public string DeadLetterQueryPageSql => """
        SELECT Id, MessageType, Body, SourceEndpoint, ExceptionType, ExceptionMessage, MessageId, CausationId, ConversationId, AttemptCount, DeadLetteredAt
        FROM conduit_dead_letters
        WHERE (@MessageType IS NULL OR MessageType = @MessageType)
          AND (@ExceptionType IS NULL OR ExceptionType = @ExceptionType)
          AND (@Since IS NULL OR DeadLetteredAt >= @Since)
          AND (@Until IS NULL OR DeadLetteredAt <= @Until)
        ORDER BY DeadLetteredAt DESC
        LIMIT @PageSize OFFSET @Offset
        """;

    /// <inheritdoc/>
    public string DeadLetterCountSql => """
        SELECT COUNT(*) FROM conduit_dead_letters
        WHERE (@MessageType IS NULL OR MessageType = @MessageType)
          AND (@ExceptionType IS NULL OR ExceptionType = @ExceptionType)
          AND (@Since IS NULL OR DeadLetteredAt >= @Since)
          AND (@Until IS NULL OR DeadLetteredAt <= @Until)
        """;

    /// <inheritdoc/>
    public string DeadLetterDeleteByIdSql => "DELETE FROM conduit_dead_letters WHERE Id = @Id";

    /// <inheritdoc/>
    public string DeadLetterDeleteByIdsSql => "DELETE FROM conduit_dead_letters WHERE Id IN ({0})";
}
