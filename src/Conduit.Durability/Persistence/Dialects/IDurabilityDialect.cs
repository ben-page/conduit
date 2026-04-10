namespace Conduit.Durability.Persistence.Dialects;

/// <summary>
/// Generates database-specific SQL for the durability tables (outbox, inbox, and dead letter queue).
/// One implementation per database engine.
/// </summary>
public interface IDurabilityDialect
{
    // Outbox

    /// <summary>Idempotent CREATE TABLE for the outbox.</summary>
    public string OutboxCreateTableSql { get; }
    /// <summary>INSERT outbox entry. Parameters: @MessageId, @ConversationId, @CausationId, @Type, @Payload, @CreatedAt.</summary>
    public string OutboxInsertEntrySql { get; }
    /// <summary>SELECT with row-level locking. Parameters: @GraceCutoff, @BatchSize.</summary>
    public string OutboxClaimPendingEntriesSql { get; }
    /// <summary>Mark entry as sent. Parameters: @Id, @SentAt.</summary>
    public string OutboxMarkSentSql { get; }
    /// <summary>Increment retry count, keep status Pending. Parameter: @Id.</summary>
    public string OutboxIncrementRetrySql { get; }
    /// <summary>Mark entry as permanently failed. Parameter: @Id.</summary>
    public string OutboxMarkFailedSql { get; }
    /// <summary>Delete completed/failed entries. Parameter: @RetentionCutoff.</summary>
    public string OutboxDeleteCompletedSql { get; }
    /// <summary>SELECT Id and MessageId with row-level locking, filtered by MessageId list. The SQL must contain a placeholder token {0} where the comma-separated @p0, @p1, ... parameter list will be inserted.</summary>
    public string OutboxClaimByMessageIdsSql { get; }

    // Inbox

    /// <summary>Idempotent CREATE TABLE for the inbox.</summary>
    public string InboxCreateTableSql { get; }
    /// <summary>INSERT with ignore/upsert semantics (no error on duplicate MessageId). Parameters: @MessageId, @ProcessedAt.</summary>
    public string InboxInsertSql { get; }
    /// <summary>SELECT EXISTS by MessageId. Parameter: @MessageId.</summary>
    public string InboxExistsSql { get; }
    /// <summary>DELETE entries older than the retention cutoff. Parameter: @Cutoff.</summary>
    public string InboxDeleteExpiredSql { get; }

    // Dead Letter Queue

    /// <summary>Idempotent CREATE TABLE for the dead letter queue.</summary>
    public string DeadLetterCreateTableSql { get; }
    /// <summary>INSERT dead letter entry. Parameters: @Id, @MessageType, @Body, @SourceEndpoint, @ExceptionType, @ExceptionMessage, @MessageId, @CausationId, @ConversationId, @AttemptCount, @DeadLetteredAt.</summary>
    public string DeadLetterInsertSql { get; }
    /// <summary>SELECT by primary key. Parameter: @Id.</summary>
    public string DeadLetterSelectByIdSql { get; }
    /// <summary>SELECT with optional filters and pagination. Parameters: @MessageType (nullable), @ExceptionType (nullable), @Since (nullable), @Until (nullable), @PageSize, @Offset.</summary>
    public string DeadLetterQueryPageSql { get; }
    /// <summary>COUNT with same filter parameters as QueryPage (excluding @PageSize, @Offset).</summary>
    public string DeadLetterCountSql { get; }
    /// <summary>DELETE by primary key. Parameter: @Id.</summary>
    public string DeadLetterDeleteByIdSql { get; }
    /// <summary>DELETE by list of IDs. The SQL must contain a placeholder token {0} where the comma-separated @p0, @p1, ... parameter list will be inserted.</summary>
    public string DeadLetterDeleteByIdsSql { get; }
}
