namespace Conduit.Durability.Inbox;

/// <summary>
/// Provides database-specific SQL for the inbox store.
/// </summary>
public interface IInboxDialect
{
    /// <summary>Idempotent CREATE TABLE for the inbox.</summary>
    public string CreateTableSql { get; }

    /// <summary>INSERT with ignore/upsert semantics (no error on duplicate MessageId).</summary>
    public string InsertSql { get; }

    /// <summary>SELECT EXISTS by MessageId.</summary>
    public string ExistsSql { get; }

    /// <summary>DELETE entries older than the retention cutoff.</summary>
    public string DeleteExpiredSql { get; }
}
