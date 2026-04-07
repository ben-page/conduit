namespace Conduit.Outbox.Configuration;

/// <summary>
/// Configuration for the transactional outbox.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>How often the relay polls for pending entries. Default: 5 seconds.</summary>
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Entries newer than this are ignored by the relay. Default: 10 seconds.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum entries claimed per relay sweep. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;
    /// <summary>Maximum relay attempts before marking failed. Default: 10.</summary>
    public int MaxRetries { get; set; } = 10;
    /// <summary>Whether to auto-create the outbox table on startup. Default: true.</summary>
    public bool AutoCreateTable { get; set; } = true;
}
