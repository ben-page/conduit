using Conduit.Outbox.Store;

namespace Conduit.Outbox.Dialects;

/// <summary>
/// SQLite dialect for the outbox store. SQLite is single-writer, so no SKIP LOCKED is needed.
/// </summary>
public sealed class SqliteDialect : IOutboxDialect
{
    /// <inheritdoc/>
    public string CreateTableSql => """
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
    public string InsertEntrySql => """
        INSERT INTO conduit_outbox (MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, RetryCount)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0)
        """;

    /// <inheritdoc/>
    public string ClaimPendingEntriesSql => """
        SELECT Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount
        FROM conduit_outbox
        WHERE Status = 0 AND CreatedAt <= @GraceCutoff
        ORDER BY Id
        LIMIT @BatchSize
        """;

    /// <inheritdoc/>
    public string MarkSentSql => "UPDATE conduit_outbox SET Status = 1, SentAt = @SentAt WHERE Id = @Id";

    /// <inheritdoc/>
    public string IncrementRetrySql => "UPDATE conduit_outbox SET RetryCount = RetryCount + 1 WHERE Id = @Id";

    /// <inheritdoc/>
    public string MarkFailedSql => "UPDATE conduit_outbox SET Status = 2 WHERE Id = @Id";

    /// <inheritdoc/>
    public string DeleteCompletedSql => "DELETE FROM conduit_outbox WHERE Status IN (1, 2) AND CreatedAt <= @RetentionCutoff";

    /// <inheritdoc/>
    public string ClaimByMessageIdsSql => """
        SELECT Id, MessageId FROM conduit_outbox
        WHERE MessageId IN ({0}) AND Status = 0
        ORDER BY Id
        """;
}
