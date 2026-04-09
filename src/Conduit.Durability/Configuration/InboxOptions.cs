namespace Conduit.Durability.Configuration;

/// <summary>
/// Configuration options for the inbox.
/// </summary>
public sealed class InboxOptions
{
    /// <summary>How long to keep processed message IDs before cleanup. Default: 30 minutes.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Interval between cleanup sweeps. Default: 5 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
