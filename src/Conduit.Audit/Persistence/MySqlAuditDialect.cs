using System.Data;

namespace Conduit.Audit.Persistence;

/// <summary>
/// MySQL 8.0+ dialect for the audit table. Requires MySqlConnector.
/// </summary>
public sealed class MySqlAuditDialect : IAuditDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.DateTime2;

    /// <inheritdoc/>
    public string CreateAuditTableSql => """
        CREATE TABLE IF NOT EXISTS conduit_audit (
            Id BIGINT AUTO_INCREMENT PRIMARY KEY,
            MessageId VARCHAR(64) NOT NULL,
            ConversationId VARCHAR(64) NOT NULL,
            CausationId VARCHAR(64) NULL,
            MessageType VARCHAR(512) NOT NULL,
            MessageKind VARCHAR(64) NOT NULL,
            ServiceName VARCHAR(256) NOT NULL,
            Payload LONGTEXT NULL,
            Properties LONGTEXT NULL,
            Timestamp DATETIME(6) NOT NULL,
            Observations LONGTEXT NOT NULL,
            INDEX IX_audit_message (MessageId),
            INDEX IX_audit_conversation (ConversationId),
            INDEX IX_audit_type_time (MessageType, Timestamp),
            INDEX IX_audit_timestamp (Timestamp)
        )
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
