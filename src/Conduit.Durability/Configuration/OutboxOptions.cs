using Conduit.Durability.Runtime.Outbox;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Configuration;

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
    /// (<see cref="AutoCreateTable"/> creates it only with a new table).
    /// </summary>
    public IDictionary<string, Type> PersistedProperties { get; } = new Dictionary<string, Type>(StringComparer.Ordinal);

    /// <summary>Refuses a <see cref="PersistedProperties"/> entry with an empty key or an unsupported type.</summary>
    /// <exception cref="OptionsValidationException">An entry is invalid; each failure names its key.</exception>
    internal void Validate()
    {
        var failures = new List<string>();
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
