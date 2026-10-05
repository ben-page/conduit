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
}
