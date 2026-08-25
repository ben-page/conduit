namespace Conduit.Jobs.Configuration;

/// <summary>Tuning for the jobs runtime.</summary>
public sealed class JobsOptions
{
    /// <summary>How often a running host renews its claim. Default 30 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How stale a claim must be before another host may take it. Default 2 minutes — four missed
    /// heartbeats. Must be a comfortable multiple of <see cref="HeartbeatInterval"/>, or a slow
    /// database produces spurious re-claims and double execution.
    /// </summary>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Durable retry attempts before a run is marked failed. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long failed runs are retained for inspection. Default 7 days.</summary>
    public TimeSpan FailedRetention { get; set; } = TimeSpan.FromDays(7);
}
