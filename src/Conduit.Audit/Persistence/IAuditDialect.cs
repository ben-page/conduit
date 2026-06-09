using System.Data;

namespace Conduit.Audit.Persistence;

/// <summary>
/// Generates database-specific SQL for the audit table.
/// One implementation per database engine.
/// </summary>
public interface IAuditDialect
{
    /// <summary>
    /// The <see cref="DbType"/> the store assigns to UTC timestamp parameters (<c>@Timestamp</c>, <c>@Cutoff</c>).
    /// Relational engines use <see cref="DbType.DateTime2"/> to bind a native datetime; Postgres uses
    /// <see cref="DbType.Object"/> so Npgsql infers <c>timestamptz</c> from the <see cref="DateTimeKind.Utc"/> value
    /// (an explicit <see cref="DbType.DateTime2"/> would resolve to <c>timestamp without time zone</c> and conflict).
    /// </summary>
    public DbType TimestampDbType { get; }

    /// <summary>Idempotent CREATE TABLE and indexes for the audit table.</summary>
    public string CreateAuditTableSql { get; }

    /// <summary>INSERT audit record. Parameters: @MessageId, @ConversationId, @CausationId, @MessageType, @MessageKind, @ServiceName, @Payload, @Properties, @Timestamp, @Observations.</summary>
    public string InsertAuditRecordSql { get; }

    /// <summary>SELECT by MessageId. Parameter: @MessageId.</summary>
    public string SelectByMessageIdSql { get; }

    /// <summary>DELETE where Timestamp is before cutoff. Parameter: @Cutoff.</summary>
    public string DeleteExpiredSql { get; }
}
