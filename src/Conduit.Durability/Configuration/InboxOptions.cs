using Microsoft.Extensions.Options;

namespace Conduit.Durability.Configuration;

/// <summary>
/// Configuration options for the inbox.
/// </summary>
public sealed class InboxOptions
{
    /// <summary>
    /// How long to keep processed message IDs before cleanup. Positive, and at most 3650 days. Default: 30 minutes.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Interval between cleanup sweeps. Positive, and at most one day. Default: 5 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Refuses a <see cref="RetentionPeriod"/> that is not positive or is above
    /// <see cref="DurationLimits.LongestRetention"/>, and a <see cref="CleanupInterval"/> that is not positive or is
    /// above <see cref="DurationLimits.LongestDuration"/>.
    /// </summary>
    /// <exception cref="OptionsValidationException">An option is invalid; each failure names it.</exception>
    internal void Validate()
    {
        var failures = new List<string>();

        // A record kept for no time deduplicates nothing, and the cleanup subtracts the retention from now.
        DurationLimits.RequirePositive(
            failures, "InboxOptions.RetentionPeriod", this.RetentionPeriod, DurationLimits.LongestRetention);

        // A zero interval turns the cleanup loop into a busy one, and a negative one throws in Task.Delay.
        DurationLimits.RequirePositive(
            failures, "InboxOptions.CleanupInterval", this.CleanupInterval, DurationLimits.LongestDuration);

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(InboxOptions), failures);
        }
    }
}
