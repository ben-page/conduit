using System.Data;

namespace Conduit.Audit.Persistence;

/// <summary>
/// PostgreSQL dialect for the audit table. Requires Npgsql.
/// Columns are snake_case and aliased back to the PascalCase names <see cref="DbAuditStore"/> reads.
/// The timestamp column is <c>timestamptz</c>; Npgsql infers it from the <see cref="DateTimeKind.Utc"/>
/// value the store binds (see <see cref="TimestampDbType"/>).
/// </summary>
public sealed class PostgresAuditDialect : IAuditDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.Object;

    /// <inheritdoc/>
    public string CreateAuditTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_audit (
            id BIGSERIAL PRIMARY KEY,
            message_id VARCHAR(64) NOT NULL,
            conversation_id VARCHAR(64) NOT NULL,
            causation_id VARCHAR(64),
            message_type VARCHAR(512) NOT NULL,
            message_kind VARCHAR(64) NOT NULL,
            service_name VARCHAR(256) NOT NULL,
            payload TEXT,
            properties TEXT,
            occurred_at TIMESTAMPTZ NOT NULL,
            observations TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_audit_message ON conduit_audit (message_id);
        CREATE INDEX IF NOT EXISTS ix_audit_conversation ON conduit_audit (conversation_id);
        CREATE INDEX IF NOT EXISTS ix_audit_type_time ON conduit_audit (message_type, occurred_at);
        CREATE INDEX IF NOT EXISTS ix_audit_timestamp ON conduit_audit (occurred_at);
        """;

    /// <inheritdoc/>
    public string InsertAuditRecordSql => """
        INSERT INTO conduit_audit (message_id, conversation_id, causation_id, message_type, message_kind, service_name, payload, properties, occurred_at, observations)
        VALUES (@MessageId, @ConversationId, @CausationId, @MessageType, @MessageKind, @ServiceName, @Payload, @Properties, @Timestamp, @Observations)
        """;

    /// <inheritdoc/>
    public string SelectByMessageIdSql => """
        SELECT id AS "Id", message_id AS "MessageId", conversation_id AS "ConversationId",
               causation_id AS "CausationId", message_type AS "MessageType", message_kind AS "MessageKind",
               service_name AS "ServiceName", payload AS "Payload", properties AS "Properties",
               occurred_at AS "Timestamp", observations AS "Observations"
        FROM conduit_audit
        WHERE message_id = @MessageId
        """;

    /// <inheritdoc/>
    public string DeleteExpiredSql => "DELETE FROM conduit_audit WHERE occurred_at < @Cutoff";
}
