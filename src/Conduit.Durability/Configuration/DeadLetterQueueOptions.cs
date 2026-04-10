namespace Conduit.Durability.Configuration;

/// <summary>
/// Options for the database-backed dead letter queue.
/// </summary>
public sealed class DeadLetterQueueOptions
{
    /// <summary>Whether to automatically create the dead letter table on startup. Default true.</summary>
    public bool AutoCreateTable { get; set; } = true;
}
