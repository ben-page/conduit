namespace Conduit.Outbox.Store;

/// <summary>
/// Generates database-specific SQL for the outbox table.
/// </summary>
public interface IOutboxDialect
{
    /// <summary>Idempotent CREATE TABLE statement.</summary>
    public string CreateTableSql { get; }
    /// <summary>INSERT statement. Parameters: @MessageId, @ConversationId, @CausationId, @Type, @Payload, @CreatedAt.</summary>
    public string InsertEntrySql { get; }
    /// <summary>SELECT with row-level locking. Parameters: @GraceCutoff, @BatchSize.</summary>
    public string ClaimPendingEntriesSql { get; }
    /// <summary>Mark entry as sent. Parameters: @Id, @SentAt.</summary>
    public string MarkSentSql { get; }
    /// <summary>Increment retry count, keep status Pending. Parameter: @Id.</summary>
    public string IncrementRetrySql { get; }
    /// <summary>Mark entry as permanently failed. Parameter: @Id.</summary>
    public string MarkFailedSql { get; }
    /// <summary>Delete completed/failed entries. Parameter: @RetentionCutoff.</summary>
    public string DeleteCompletedSql { get; }
    /// <summary>SELECT Id and MessageId with row-level locking, filtered by MessageId list. The SQL must contain a placeholder token {0} where the comma-separated @p0, @p1, ... parameter list will be inserted.</summary>
    public string ClaimByMessageIdsSql { get; }
}
