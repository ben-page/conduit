using System.Threading.Channels;
using Microsoft.Extensions.Options;

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

    // The ceilings mirror Conduit.Durability's DurationLimits (LongestDuration, LongestRetention), which this package
    // cannot reference: one day for an interval, a wait or a timeout, 3650 days for a retention. A timer, Task.Delay or
    // CancelAfter cannot hold much past 49.7 days, and a duration subtracted from now overflows near TimeSpan.MaxValue.
    private static readonly TimeSpan LongestDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan LongestRetention = TimeSpan.FromDays(3650);

    /// <summary>Maximum number of records per batch before publishing. Positive. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The service name included in audit batches. Defaults to "unknown" if not set.</summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// How long a dispatch group may go without an observation before it is considered stale and evicted (sealed as
    /// failed). Measured from the group's last observation, not its first, so a long dispatch that keeps observing stays
    /// open. Positive, and at most one day. Default 5 minutes.
    /// </summary>
    public TimeSpan StaleGroupTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of audit records buffered in the channel before backpressure applies. Positive. Default 10,000.</summary>
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>
    /// How long the batch publisher keeps draining queued records when the host stops, at most; the host's own shutdown
    /// deadline ends it sooner. The records still unpublished then are counted in one Warning (EventId 520,
    /// <c>AuditRecordsAbandoned</c>). Not negative (zero abandons what is queued at once), and at most one day. Default
    /// 5 seconds.
    /// </summary>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(5);

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

    /// <summary>How long stored audit records are retained before purging. Positive, and at most 3650 days. Default 90 days.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the purge process runs to remove expired records. Positive, and at most one day. Default 24 hours.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Refuses options the runtime cannot work with: a <see cref="BatchSize"/> or <see cref="ChannelCapacity"/> that is
    /// not positive; a <see cref="StaleGroupTimeout"/> or <see cref="PurgeInterval"/> that is not positive or is above
    /// one day; a negative <see cref="ShutdownGrace"/> or one above one day; an enabled <see cref="MaxPayloadBytes"/>
    /// below <see cref="MinimumMaxPayloadBytes"/>; and a <see cref="RetentionPeriod"/> that is not positive or is above
    /// 3650 days. <c>AddAudit</c> and <c>AddAuditListener</c> call it once their callback has run.
    /// </summary>
    /// <exception cref="OptionsValidationException">An option is invalid; each failure names it.</exception>
    internal void Validate()
    {
        var failures = new List<string>();

        // A batch of nothing drains nothing while the channel stays readable: the publisher would spin on one record.
        RequirePositive(failures, nameof(this.BatchSize), this.BatchSize);
        RequirePositive(failures, nameof(this.ChannelCapacity), this.ChannelCapacity);

        // The sweep's PeriodicTimer is set from it, and a period that is not positive throws inside the publisher.
        RequirePositive(failures, nameof(this.StaleGroupTimeout), this.StaleGroupTimeout, LongestDuration);

        // The stop's CancelAfter throws for a negative delay; zero drains nothing and abandons what is queued.
        if (this.ShutdownGrace < TimeSpan.Zero)
        {
            failures.Add($"AuditOptions.ShutdownGrace is {this.ShutdownGrace}; it cannot be negative.");
        }
        else if (this.ShutdownGrace > LongestDuration)
        {
            failures.Add($"AuditOptions.ShutdownGrace is {this.ShutdownGrace}; it can be at most {LongestDuration}.");
        }

        if (this.MaxPayloadBytes is > 0 and < MinimumMaxPayloadBytes)
        {
            failures.Add(
                $"AuditOptions.MaxPayloadBytes is {this.MaxPayloadBytes}; it must be 0 or less (no cap) or at least " +
                $"{MinimumMaxPayloadBytes}, so the $truncated marker fits.");
        }

        // The purge waits it between passes (a zero interval purges in a loop) and subtracts the retention from now (a
        // zero retention purges every record as it is written).
        RequirePositive(failures, nameof(this.PurgeInterval), this.PurgeInterval, LongestDuration);
        RequirePositive(failures, nameof(this.RetentionPeriod), this.RetentionPeriod, LongestRetention);

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(AuditOptions), failures);
        }
    }

    private static void RequirePositive(List<string> failures, string option, int value)
    {
        if (value <= 0)
        {
            failures.Add($"AuditOptions.{option} is {value}; it must be positive.");
        }
    }

    private static void RequirePositive(List<string> failures, string option, TimeSpan value, TimeSpan longest)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"AuditOptions.{option} is {value}; it must be positive.");
        }
        else if (value > longest)
        {
            failures.Add($"AuditOptions.{option} is {value}; it can be at most {longest}.");
        }
    }
}
