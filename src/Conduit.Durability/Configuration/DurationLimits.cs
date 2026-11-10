namespace Conduit.Durability.Configuration;

/// <summary>
/// The bounds the durability options hold their durations to, and the checks that apply them. A duration that is
/// added to or subtracted from now throws near <see cref="TimeSpan.MaxValue"/>, and <see cref="Task.Delay(TimeSpan)"/>
/// throws for a negative one, so every duration an option holds has a floor and a ceiling.
/// </summary>
internal static class DurationLimits
{
    /// <summary>
    /// The longest an interval, a wait or a timeout may be: one day. It bounds <see cref="OutboxOptions.RelayInterval"/>,
    /// <see cref="OutboxOptions.CleanupInterval"/>, <see cref="OutboxOptions.GracePeriod"/>,
    /// <see cref="OutboxOptions.MaxBackoff"/>, <see cref="OutboxOptions.ClaimTimeout"/> and
    /// <see cref="InboxOptions.CleanupInterval"/>.
    /// </summary>
    internal static readonly TimeSpan LongestDuration = TimeSpan.FromDays(1);

    /// <summary>
    /// The longest a retention may be: 3650 days. It bounds <see cref="OutboxOptions.CompletedRetention"/>,
    /// <see cref="OutboxOptions.FailedRetention"/> and <see cref="InboxOptions.RetentionPeriod"/>. An outbox
    /// retention of <see cref="TimeSpan.Zero"/>, not <see cref="TimeSpan.MaxValue"/>, keeps entries forever.
    /// </summary>
    internal static readonly TimeSpan LongestRetention = TimeSpan.FromDays(3650);

    /// <summary>Adds a failure naming <paramref name="option"/> when its value is not positive or is above <paramref name="longest"/>.</summary>
    internal static void RequirePositive(List<string> failures, string option, TimeSpan value, TimeSpan longest)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{option} is {value}; it must be positive.");
        }
        else if (value > longest)
        {
            failures.Add($"{option} is {value}; it can be at most {longest}.");
        }
    }

    /// <summary>
    /// Adds a failure naming <paramref name="option"/> when its value is negative or is above <paramref name="longest"/>;
    /// <paramref name="ceilingAdvice"/> follows the ceiling's sentence.
    /// </summary>
    internal static void RequireNotNegative(
        List<string> failures,
        string option,
        TimeSpan value,
        TimeSpan longest,
        string ceilingAdvice = "")
    {
        if (value < TimeSpan.Zero)
        {
            failures.Add($"{option} is {value}; it cannot be negative.");
        }
        else if (value > longest)
        {
            failures.Add($"{option} is {value}; it can be at most {longest}.{ceilingAdvice}");
        }
    }
}
