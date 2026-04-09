namespace Conduit.Durability.Persistence.Dialects;

/// <summary>SQL Server dialect. Uses ROWLOCK + UPDLOCK + READPAST for concurrent claiming.</summary>
public sealed class SqlServerDialect : IDurabilityDialect
{
    // Outbox

    /// <inheritdoc/>
    public string OutboxCreateTableSql => """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'conduit_outbox')
        CREATE TABLE conduit_outbox (
            Id BIGINT IDENTITY(1,1) PRIMARY KEY,
            MessageId NVARCHAR(64) NOT NULL,
            ConversationId NVARCHAR(64) NOT NULL,
            CausationId NVARCHAR(64) NULL,
            Type NVARCHAR(512) NOT NULL,
            Payload NVARCHAR(MAX) NOT NULL,
            Status TINYINT NOT NULL DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL,
            SentAt DATETIME2 NULL,
            RetryCount INT NOT NULL DEFAULT 0,
            INDEX IX_conduit_outbox_pending (Status, CreatedAt) WHERE Status = 0
        )
        """;

    /// <inheritdoc/>
    public string OutboxInsertEntrySql => """
        INSERT INTO conduit_outbox (MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, RetryCount)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0)
        """;

    /// <inheritdoc/>
    public string OutboxClaimPendingEntriesSql => """
        SELECT TOP (@BatchSize) Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount
        FROM conduit_outbox WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE Status = 0 AND CreatedAt <= @GraceCutoff
        ORDER BY Id
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
        SELECT Id, MessageId FROM conduit_outbox WITH (ROWLOCK, UPDLOCK, READPAST)
        WHERE MessageId IN ({0}) AND Status = 0
        ORDER BY Id
        """;

    // Inbox

    /// <inheritdoc/>
    public string InboxCreateTableSql => """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'conduit_inbox')
        CREATE TABLE conduit_inbox (
            MessageId NVARCHAR(64) PRIMARY KEY,
            ProcessedAt DATETIME2 NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InboxInsertSql => """
        INSERT INTO conduit_inbox (MessageId, ProcessedAt)
        SELECT @MessageId, @ProcessedAt
        WHERE NOT EXISTS (SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId)
        """;

    /// <inheritdoc/>
    public string InboxExistsSql => "SELECT 1 FROM conduit_inbox WHERE MessageId = @MessageId";

    /// <inheritdoc/>
    public string InboxDeleteExpiredSql => "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";
}
