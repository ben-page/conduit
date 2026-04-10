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

    // Dead Letter Queue

    /// <inheritdoc/>
    public string DeadLetterCreateTableSql => """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'conduit_dead_letters')
        CREATE TABLE conduit_dead_letters (
            Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            MessageType NVARCHAR(500) NOT NULL,
            Body VARBINARY(MAX) NOT NULL,
            SourceEndpoint NVARCHAR(200) NOT NULL,
            ExceptionType NVARCHAR(500) NULL,
            ExceptionMessage NVARCHAR(MAX) NULL,
            MessageId NVARCHAR(64) NULL,
            CausationId NVARCHAR(64) NULL,
            ConversationId NVARCHAR(64) NULL,
            AttemptCount INT NOT NULL DEFAULT 1,
            DeadLetteredAt DATETIMEOFFSET NOT NULL,
            INDEX IX_conduit_dead_letters_type (MessageType),
            INDEX IX_conduit_dead_letters_timestamp (DeadLetteredAt)
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
        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
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
