namespace Conduit.Durability.Persistence.Dialects;

/// <summary>PostgreSQL dialect. Uses FOR UPDATE SKIP LOCKED for concurrent claiming.</summary>
public sealed class PostgresDialect : IDurabilityDialect
{
    // Outbox

    /// <inheritdoc/>
    public string OutboxCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_outbox (
            id BIGSERIAL PRIMARY KEY,
            message_id VARCHAR(64) NOT NULL,
            conversation_id VARCHAR(64) NOT NULL,
            causation_id VARCHAR(64),
            type VARCHAR(512) NOT NULL,
            payload TEXT NOT NULL,
            status SMALLINT NOT NULL DEFAULT 0,
            created_at TIMESTAMPTZ NOT NULL,
            sent_at TIMESTAMPTZ,
            retry_count INT NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_conduit_outbox_pending ON conduit_outbox (status, created_at) WHERE status = 0;
        """;

    /// <inheritdoc/>
    public string OutboxInsertEntrySql => """
        INSERT INTO conduit_outbox (message_id, conversation_id, causation_id, type, payload, status, created_at, retry_count)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0)
        """;

    /// <inheritdoc/>
    public string OutboxClaimPendingEntriesSql => """
        SELECT id AS "Id", message_id AS "MessageId", conversation_id AS "ConversationId",
               causation_id AS "CausationId", type AS "Type", payload AS "Payload",
               status AS "Status", created_at AS "CreatedAt", sent_at AS "SentAt", retry_count AS "RetryCount"
        FROM conduit_outbox
        WHERE status = 0 AND created_at <= @GraceCutoff
        ORDER BY id
        LIMIT @BatchSize
        FOR UPDATE SKIP LOCKED
        """;

    /// <inheritdoc/>
    public string OutboxMarkSentSql => "UPDATE conduit_outbox SET status = 1, sent_at = @SentAt WHERE id = @Id";

    /// <inheritdoc/>
    public string OutboxIncrementRetrySql => "UPDATE conduit_outbox SET retry_count = retry_count + 1 WHERE id = @Id";

    /// <inheritdoc/>
    public string OutboxMarkFailedSql => "UPDATE conduit_outbox SET status = 2 WHERE id = @Id";

    /// <inheritdoc/>
    public string OutboxDeleteCompletedSql => "DELETE FROM conduit_outbox WHERE status IN (1, 2) AND created_at <= @RetentionCutoff";

    /// <inheritdoc/>
    public string OutboxClaimByMessageIdsSql => """
        SELECT id AS "Id", message_id AS "MessageId" FROM conduit_outbox
        WHERE message_id IN ({0}) AND status = 0
        ORDER BY id
        FOR UPDATE SKIP LOCKED
        """;

    // Inbox

    /// <inheritdoc/>
    public string InboxCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_inbox (
            message_id TEXT PRIMARY KEY,
            processed_at TEXT NOT NULL
        )
        """;

    /// <inheritdoc/>
    public string InboxInsertSql => """
        INSERT INTO conduit_inbox (message_id, processed_at)
        VALUES (@MessageId, @ProcessedAt)
        ON CONFLICT (message_id) DO NOTHING
        """;

    /// <inheritdoc/>
    public string InboxExistsSql => "SELECT 1 FROM conduit_inbox WHERE message_id = @MessageId LIMIT 1";

    /// <inheritdoc/>
    public string InboxDeleteExpiredSql => "DELETE FROM conduit_inbox WHERE processed_at < @Cutoff";

    // Dead Letter Queue

    /// <inheritdoc/>
    public string DeadLetterCreateTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_dead_letters (
            id UUID NOT NULL PRIMARY KEY,
            message_type VARCHAR(500) NOT NULL,
            body BYTEA NOT NULL,
            source_endpoint VARCHAR(200) NOT NULL,
            exception_type VARCHAR(500),
            exception_message TEXT,
            message_id VARCHAR(64),
            causation_id VARCHAR(64),
            conversation_id VARCHAR(64),
            attempt_count INT NOT NULL DEFAULT 1,
            dead_lettered_at TIMESTAMPTZ NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_conduit_dead_letters_type ON conduit_dead_letters (message_type);
        CREATE INDEX IF NOT EXISTS ix_conduit_dead_letters_timestamp ON conduit_dead_letters (dead_lettered_at);
        """;

    /// <inheritdoc/>
    public string DeadLetterInsertSql => """
        INSERT INTO conduit_dead_letters (id, message_type, body, source_endpoint, exception_type, exception_message, message_id, causation_id, conversation_id, attempt_count, dead_lettered_at)
        VALUES (@Id, @MessageType, @Body, @SourceEndpoint, @ExceptionType, @ExceptionMessage, @MessageId, @CausationId, @ConversationId, @AttemptCount, @DeadLetteredAt)
        """;

    /// <inheritdoc/>
    public string DeadLetterSelectByIdSql => """
        SELECT id AS "Id", message_type AS "MessageType", body AS "Body", source_endpoint AS "SourceEndpoint",
               exception_type AS "ExceptionType", exception_message AS "ExceptionMessage",
               message_id AS "MessageId", causation_id AS "CausationId", conversation_id AS "ConversationId",
               attempt_count AS "AttemptCount", dead_lettered_at AS "DeadLetteredAt"
        FROM conduit_dead_letters WHERE id = @Id
        """;

    /// <inheritdoc/>
    public string DeadLetterQueryPageSql => """
        SELECT id AS "Id", message_type AS "MessageType", body AS "Body", source_endpoint AS "SourceEndpoint",
               exception_type AS "ExceptionType", exception_message AS "ExceptionMessage",
               message_id AS "MessageId", causation_id AS "CausationId", conversation_id AS "ConversationId",
               attempt_count AS "AttemptCount", dead_lettered_at AS "DeadLetteredAt"
        FROM conduit_dead_letters
        WHERE (@MessageType IS NULL OR message_type = @MessageType)
          AND (@ExceptionType IS NULL OR exception_type = @ExceptionType)
          AND (@Since IS NULL OR dead_lettered_at >= @Since)
          AND (@Until IS NULL OR dead_lettered_at <= @Until)
        ORDER BY dead_lettered_at DESC
        LIMIT @PageSize OFFSET @Offset
        """;

    /// <inheritdoc/>
    public string DeadLetterCountSql => """
        SELECT COUNT(*) FROM conduit_dead_letters
        WHERE (@MessageType IS NULL OR message_type = @MessageType)
          AND (@ExceptionType IS NULL OR exception_type = @ExceptionType)
          AND (@Since IS NULL OR dead_lettered_at >= @Since)
          AND (@Until IS NULL OR dead_lettered_at <= @Until)
        """;

    /// <inheritdoc/>
    public string DeadLetterDeleteByIdSql => "DELETE FROM conduit_dead_letters WHERE id = @Id";

    /// <inheritdoc/>
    public string DeadLetterDeleteByIdsSql => "DELETE FROM conduit_dead_letters WHERE id IN ({0})";
}
