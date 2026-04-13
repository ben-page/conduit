using System.Threading.Channels;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Configuration options for the audit trail subsystem.
/// Controls batching behavior, channel capacity, backpressure, and retention policies.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>Maximum number of records per batch before publishing.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Maximum time to wait before flushing a partial batch.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The service name included in audit batches. Defaults to "unknown" if not set.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Time after which an incomplete dispatch group is considered stale and evicted.</summary>
    public TimeSpan StaleGroupTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of audit records buffered in the channel before backpressure applies.</summary>
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>Behavior when the channel is full. Defaults to dropping the oldest record.</summary>
    public BoundedChannelFullMode BackpressureMode { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <summary>How long stored audit records are retained before purging.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the purge process runs to remove expired records.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Interval at which metrics are aggregated from stored audit data.</summary>
    public TimeSpan MetricsAggregationInterval { get; set; } = TimeSpan.FromHours(1);
}
