namespace Conduit.Audit.Persistence;

/// <summary>
/// Module-owned identifiers for the audit table. The store composes its DDL/CRUD SQL from these
/// constants plus the engine type vocabulary on <see cref="Conduit.Persistence.ISqlDialect"/>, so the
/// table and column names stay owned by the audit module while engine-specific syntax stays in the
/// dialect (mirroring durability's <c>OutboxTableSchema</c>).
/// </summary>
public static class AuditTableSchema
{
    /// <summary>The audit table name.</summary>
    public const string TableName = "conduit_audit";

    /// <summary>Auto-incrementing surrogate primary key column.</summary>
    public const string IdColumn = "Id";

    /// <summary>The dispatch message identifier column.</summary>
    public const string MessageIdColumn = "MessageId";

    /// <summary>The conversation identifier column.</summary>
    public const string ConversationIdColumn = "ConversationId";

    /// <summary>The causation identifier column (nullable).</summary>
    public const string CausationIdColumn = "CausationId";

    /// <summary>The full message type name column.</summary>
    public const string MessageTypeColumn = "MessageType";

    /// <summary>The message kind column (command/query/event).</summary>
    public const string MessageKindColumn = "MessageKind";

    /// <summary>The originating service name column.</summary>
    public const string ServiceNameColumn = "ServiceName";

    /// <summary>The serialized payload column (nullable).</summary>
    public const string PayloadColumn = "Payload";

    /// <summary>The serialized properties column (nullable).</summary>
    public const string PropertiesColumn = "Properties";

    /// <summary>The dispatch timestamp column.</summary>
    public const string TimestampColumn = "Timestamp";

    /// <summary>The serialized observation list column.</summary>
    public const string ObservationsColumn = "Observations";
}
