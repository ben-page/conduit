using Conduit.Durability.Runtime.Outbox;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Configuration;

/// <summary>
/// Configuration for the transactional outbox.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>The longest <see cref="MaxBackoff"/> and <see cref="ClaimTimeout"/> may be: one day.</summary>
    internal static readonly TimeSpan LongestDuration = TimeSpan.FromDays(1);

    /// <summary>How often the relay polls for pending entries. Default: 5 seconds.</summary>
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Entries newer than this are ignored by the relay. Default: 10 seconds.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum entries claimed per relay sweep. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;
    /// <summary>Maximum relay attempts before marking failed. Default: 10.</summary>
    public int MaxRetries { get; set; } = 10;

    /// <summary>
    /// How long an entry waits for its next attempt after its first failed send. Each failed attempt after it
    /// doubles the wait, up to <see cref="MaxBackoff"/>, so a broker that is down for minutes does not use up
    /// <see cref="MaxRetries"/> in seconds. Default: 5 seconds.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest an entry waits between two attempts (see <see cref="InitialBackoff"/>). At most one day.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a claimed entry stays its worker's. A claim older than this counts as abandoned (its worker crashed or
    /// was stopped mid-send) and another relay may claim the entry and deliver it again, so a crash never strands a
    /// message. A worker still delivering past it has lost the claim: its outcome for the entry is refused (logged as
    /// <c>ClaimLost</c>, EventId 321) and the row stays as the new claim leaves it. Keep it well above the longest
    /// delivery, local listeners and transport send together. Positive, and at most one day. Default: 5 minutes.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether to auto-create the outbox table on startup. Default: true.</summary>
    public bool AutoCreateTable { get; set; } = true;

    /// <summary>Sent entries older than this are deleted by the relay's periodic cleanup.
    /// <see cref="TimeSpan.Zero"/> disables it. Default: 7 days.</summary>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Failed entries (retries exhausted) older than this are deleted by the relay's periodic cleanup.
    /// <see langword="null"/> (the default) follows <see cref="CompletedRetention"/>, which is how Failed rows were
    /// treated before this option existed. <see cref="TimeSpan.Zero"/> keeps them forever: a Failed row is the only
    /// copy of a side effect that never ran.</summary>
    public TimeSpan? FailedRetention { get; set; }

    /// <summary>How often the relay runs retention cleanup. Default: 1 hour.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Capture outbound messages into the outbox on the caller's transaction, but never deliver them: no
    /// <see cref="Runtime.Outbox.OutboxRelay"/> is registered and <see cref="Runtime.Outbox.IOutboxFlusher"/>
    /// is a no-op. For a process that shares its outbox table with another process whose relay owns
    /// delivery — a second relay could claim a row whose listener lives only in the other process and mark
    /// it sent with nobody having handled it. Messages published with no ambient transaction are not
    /// captured and still deliver locally, exactly as without an outbox. Default: <see langword="false"/>.
    /// </summary>
    public bool CaptureOnly { get; set; }

    /// <summary>
    /// The dispatch properties (<see cref="Conduit.Runtime.MessageContext.Properties"/>) an outbox entry carries, each
    /// key with the type its value has. The publishing dispatch's values for these keys are written to the entry's
    /// <c>Properties</c> column, and both delivery paths — the post-commit flush and the relay — restore them on the
    /// context the listeners run under, so a listener (and every dispatch it makes) sees, for example, the publishing
    /// user. Supported types are <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="bool"/> and
    /// <see cref="Guid"/>; <c>AddOutbox</c> refuses any other with <see cref="OptionsValidationException"/>.
    /// Default: empty — nothing is carried, and the store neither writes nor reads the <c>Properties</c> column, so
    /// an outbox table created before the column existed keeps working. A host that lists a key must have the column
    /// (<see cref="AutoCreateTable"/> creates it only with a new table). Keys are case-insensitive, as property names
    /// are: <c>"UserId"</c> and <c>"userid"</c> are one entry.
    /// </summary>
    public IDictionary<string, Type> PersistedProperties { get; } =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a <see cref="PersistedProperties"/> entry with an empty key or an unsupported type, a negative
    /// <see cref="InitialBackoff"/>, a <see cref="MaxBackoff"/> below it or above <see cref="LongestDuration"/>, and a
    /// <see cref="ClaimTimeout"/> that is not positive or is above <see cref="LongestDuration"/>.
    /// </summary>
    /// <exception cref="OptionsValidationException">An option is invalid; each failure names it.</exception>
    internal void Validate()
    {
        var failures = new List<string>();
        if (this.InitialBackoff < TimeSpan.Zero)
        {
            failures.Add($"OutboxOptions.InitialBackoff is {this.InitialBackoff}; it cannot be negative.");
        }

        if (this.MaxBackoff < this.InitialBackoff)
        {
            failures.Add(
                $"OutboxOptions.MaxBackoff is {this.MaxBackoff}, below OutboxOptions.InitialBackoff ({this.InitialBackoff}).");
        }

        // A failed send adds the backoff to now, which throws near TimeSpan.MaxValue.
        if (this.MaxBackoff > LongestDuration)
        {
            failures.Add($"OutboxOptions.MaxBackoff is {this.MaxBackoff}; it can be at most {LongestDuration}.");
        }

        // A claim that is stale at once would let any relay take over an entry another is still delivering; the claim
        // subtracts the timeout from now, which throws near TimeSpan.MaxValue.
        if (this.ClaimTimeout <= TimeSpan.Zero)
        {
            failures.Add($"OutboxOptions.ClaimTimeout is {this.ClaimTimeout}; it must be positive.");
        }
        else if (this.ClaimTimeout > LongestDuration)
        {
            failures.Add($"OutboxOptions.ClaimTimeout is {this.ClaimTimeout}; it can be at most {LongestDuration}.");
        }

        foreach (var (key, type) in this.PersistedProperties)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                failures.Add("OutboxOptions.PersistedProperties has an empty key.");
            }
            else if (type is null || !OutboxProperties.SupportedTypes.Contains(type))
            {
                failures.Add(
                    $"OutboxOptions.PersistedProperties['{key}'] is {type?.FullName ?? "null"}; " +
                    "the supported types are string, int, long, bool and Guid.");
            }
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(OutboxOptions), failures);
        }
    }
}
