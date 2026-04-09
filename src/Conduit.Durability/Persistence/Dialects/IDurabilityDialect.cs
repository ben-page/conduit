namespace Conduit.Durability.Persistence.Dialects;

/// <summary>
/// Generates database-specific SQL for the durability tables (outbox and inbox).
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
}
