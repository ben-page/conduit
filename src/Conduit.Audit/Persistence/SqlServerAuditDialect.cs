using System.Data;

namespace Conduit.Audit.Persistence;

/// <summary>
/// SQL Server dialect for the audit table. Requires Microsoft.Data.SqlClient.
/// </summary>
public sealed class SqlServerAuditDialect : IAuditDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.DateTime2;

    /// <inheritdoc/>
    public string CreateAuditTableSql => """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'conduit_audit')
        CREATE TABLE conduit_audit (
            Id BIGINT IDENTITY(1,1) PRIMARY KEY,
            MessageId NVARCHAR(64) NOT NULL,
            ConversationId NVARCHAR(64) NOT NULL,
            CausationId NVARCHAR(64) NULL,
            MessageType NVARCHAR(512) NOT NULL,
            MessageKind NVARCHAR(64) NOT NULL,
            ServiceName NVARCHAR(256) NOT NULL,
            Payload NVARCHAR(MAX) NULL,
            Properties NVARCHAR(MAX) NULL,
            Timestamp DATETIME2 NOT NULL,
            Observations NVARCHAR(MAX) NOT NULL,
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
