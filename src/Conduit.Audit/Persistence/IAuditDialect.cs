namespace Conduit.Audit.Persistence;

/// <summary>
/// Generates database-specific SQL for the audit table.
/// One implementation per database engine.
/// </summary>
public interface IAuditDialect
{
    /// <summary>Idempotent CREATE TABLE and indexes for the audit table.</summary>
    public string CreateAuditTableSql { get; }

    /// <summary>INSERT audit record. Parameters: @MessageId, @ConversationId, @CausationId, @MessageType, @MessageKind, @ServiceName, @Payload, @Properties, @Timestamp, @Observations.</summary>
    public string InsertAuditRecordSql { get; }

    /// <summary>SELECT by MessageId. Parameter: @MessageId.</summary>
    public string SelectByMessageIdSql { get; }

    /// <summary>DELETE where Timestamp is before cutoff. Parameter: @Cutoff.</summary>
    public string DeleteExpiredSql { get; }
}
