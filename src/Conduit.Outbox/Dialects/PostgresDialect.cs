namespace Conduit.Outbox.Dialects;

/// <summary>PostgreSQL dialect. Uses FOR UPDATE SKIP LOCKED for concurrent claiming.</summary>
public sealed class PostgresDialect : IOutboxDialect
{
    /// <inheritdoc/>
    public string CreateTableSql => """
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
    public string InsertEntrySql => """
        INSERT INTO conduit_outbox (message_id, conversation_id, causation_id, type, payload, status, created_at, retry_count)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0)
        """;

    /// <inheritdoc/>
    public string ClaimPendingEntriesSql => """
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
    public string MarkSentSql => "UPDATE conduit_outbox SET status = 1, sent_at = @SentAt WHERE id = @Id";

    /// <inheritdoc/>
    public string IncrementRetrySql => "UPDATE conduit_outbox SET retry_count = retry_count + 1 WHERE id = @Id";

    /// <inheritdoc/>
    public string MarkFailedSql => "UPDATE conduit_outbox SET status = 2 WHERE id = @Id";

    /// <inheritdoc/>
    public string DeleteCompletedSql => "DELETE FROM conduit_outbox WHERE status IN (1, 2) AND created_at <= @RetentionCutoff";
}
