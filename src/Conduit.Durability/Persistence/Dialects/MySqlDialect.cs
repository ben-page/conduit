namespace Conduit.Durability.Persistence.Dialects;

/// <summary>MySQL 8.0+ dialect. Uses FOR UPDATE SKIP LOCKED for concurrent claiming.</summary>
public sealed class MySqlDialect : IDurabilityDialect
{
    // Outbox

    /// <inheritdoc/>
    public string OutboxCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_outbox (
            Id BIGINT AUTO_INCREMENT PRIMARY KEY,
            MessageId VARCHAR(64) NOT NULL,
            ConversationId VARCHAR(64) NOT NULL,
            CausationId VARCHAR(64) NULL,
            Type VARCHAR(512) NOT NULL,
            Payload LONGTEXT NOT NULL,
            Status TINYINT NOT NULL DEFAULT 0,
            CreatedAt DATETIME(6) NOT NULL,
            SentAt DATETIME(6) NULL,
            RetryCount INT NOT NULL DEFAULT 0,
            INDEX IX_conduit_outbox_pending (Status, CreatedAt)
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
        FOR UPDATE SKIP LOCKED
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
        FOR UPDATE SKIP LOCKED
        """;

    // Inbox

    /// <inheritdoc/>
    public string InboxCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_inbox (
            MessageId VARCHAR(64) PRIMARY KEY,
            ProcessedAt DATETIME(6) NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InboxInsertSql => """
        INSERT IGNORE INTO conduit_inbox (MessageId, ProcessedAt)
        VALUES (@MessageId, @ProcessedAt)
        """;

    /// <inheritdoc/>
    public string InboxExistsSql => "SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId LIMIT 1";

    /// <inheritdoc/>
    public string InboxDeleteExpiredSql => "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";
}
