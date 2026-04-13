namespace Conduit.Audit.Persistence;

/// <summary>
/// SQLite dialect for the audit table.
/// </summary>
public sealed class SqliteAuditDialect : IAuditDialect
{
    /// <inheritdoc/>
    public string CreateAuditTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_audit (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MessageId TEXT NOT NULL,
            ConversationId TEXT NOT NULL,
            CausationId TEXT,
            MessageType TEXT NOT NULL,
            MessageKind TEXT NOT NULL,
            ServiceName TEXT NOT NULL,
            Payload TEXT,
            Properties TEXT,
            Timestamp TEXT NOT NULL,
            Observations TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_audit_message ON conduit_audit (MessageId);
        CREATE INDEX IF NOT EXISTS IX_audit_conversation ON conduit_audit (ConversationId);
        CREATE INDEX IF NOT EXISTS IX_audit_type_time ON conduit_audit (MessageType, Timestamp);
        CREATE INDEX IF NOT EXISTS IX_audit_timestamp ON conduit_audit (Timestamp);
        """;

    /// <inheritdoc/>
    public string InsertAuditRecordSql => """
        INSERT INTO conduit_audit (MessageId, ConversationId, CausationId, MessageType, MessageKind, ServiceName, Payload, Properties, Timestamp, Observations)
        VALUES (@MessageId, @ConversationId, @CausationId, @MessageType, @MessageKind, @ServiceName, @Payload, @Properties, @Timestamp, @Observations)
        """;

    /// <inheritdoc/>
    public string SelectByMessageIdSql => """
        SELECT Id, MessageId, ConversationId, CausationId, MessageType, MessageKind, ServiceName, Payload, Properties, Timestamp, Observations
        FROM conduit_audit
        WHERE MessageId = @MessageId
        """;

    /// <inheritdoc/>
    public string DeleteExpiredSql => "DELETE FROM conduit_audit WHERE Timestamp < @Cutoff";
}
