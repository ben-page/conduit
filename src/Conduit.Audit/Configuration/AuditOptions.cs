using System.Threading.Channels;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Configuration options for the audit trail subsystem.
/// Controls batching behavior, channel capacity, backpressure, and retention policies.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>The default <see cref="MaxPayloadBytes"/>: 64 KB.</summary>
    public const int DefaultMaxPayloadBytes = 64 * 1024;

    /// <summary>The smallest enabled <see cref="MaxPayloadBytes"/>: 1 KB, so the <c>$truncated</c> marker always fits.</summary>
    public const int MinimumMaxPayloadBytes = 1024;

    /// <summary>Maximum number of records per batch before publishing.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The service name included in audit batches. Defaults to "unknown" if not set.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Time after which an incomplete dispatch group is considered stale and evicted.</summary>
    public TimeSpan StaleGroupTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of audit records buffered in the channel before backpressure applies.</summary>
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>Behavior when the channel is full. Defaults to dropping the oldest record.</summary>
    public BoundedChannelFullMode BackpressureMode { get; set; } = BoundedChannelFullMode.DropOldest;

    /// <summary>
    /// The largest request body, in UTF-8 bytes, a sealed record keeps. Default 64 KB (<see cref="DefaultMaxPayloadBytes"/>);
    /// 0 or less means no cap, and an enabled cap below <see cref="MinimumMaxPayloadBytes"/> is refused.
    /// </summary>
    /// <remarks>
    /// Applied when the record seals, to the body as serialized. A body at or under the cap is kept byte for byte. A body
    /// over it whose root is an object is kept with its longest string values cut to one common length (each keeps its
    /// leading characters and ends in <c>…</c>; a redacted placeholder is never cut) and a top-level
    /// <c>"$truncated": { "bytes": &lt;original UTF-8 bytes&gt;, "cap": &lt;cap&gt; }</c> member. When the root is not an object,
    /// or no cut fits, the body is that marker alone. The stored body is always valid JSON within the cap.
    /// </remarks>
    public int MaxPayloadBytes { get; set; } = DefaultMaxPayloadBytes;

    /// <summary>How long stored audit records are retained before purging.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the purge process runs to remove expired records.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(24);
}
