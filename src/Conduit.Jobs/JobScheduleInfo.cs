namespace Conduit.Jobs;

/// <summary>A declared schedule and the durable state it has accumulated.</summary>
/// <param name="Name">The schedule's name.</param>
/// <param name="NextOccurrence">
/// When it is next due, computed from the declaration rather than read from the database — the same
/// computation every host makes.
/// </param>
/// <param name="LastCompletedOccurrence">The most recent occurrence that reached a terminal outcome.</param>
/// <param name="IsPaused">Whether future occurrences are suppressed.</param>
/// <param name="PausedAt">When it was paused.</param>
public sealed record JobScheduleInfo(
    string Name,
    DateTimeOffset? NextOccurrence,
    DateTimeOffset? LastCompletedOccurrence,
    bool IsPaused,
    DateTimeOffset? PausedAt);
