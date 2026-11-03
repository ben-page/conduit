namespace Conduit.Jobs;

/// <summary>A declared schedule and the durable state it has accumulated.</summary>
/// <param name="Name">The schedule's name.</param>
/// <param name="NextOccurrence">
/// When it is next due, computed from the declaration rather than read from the database — the same
/// computation every host makes. Always <see langword="null"/> for an <see cref="IsOnDemand"/>
/// schedule, which has no timetable.
/// </param>
/// <param name="LastCompletedOccurrence">
/// The most recent occurrence that reached a terminal outcome. A manual run is not an occurrence, so
/// an on-demand schedule's runs never move it.
/// </param>
/// <param name="IsPaused">Whether future occurrences are suppressed.</param>
/// <param name="PausedAt">When it was paused.</param>
public sealed record JobScheduleInfo(
    string Name,
    DateTimeOffset? NextOccurrence,
    DateTimeOffset? LastCompletedOccurrence,
    bool IsPaused,
    DateTimeOffset? PausedAt)
{
    /// <summary>
    /// Whether the schedule was declared with <c>OnDemand()</c>: it has no timetable, so it never runs
    /// on its own and only <see cref="IJobs.TriggerNowAsync"/> starts it. <see langword="false"/> for a
    /// cron or interval schedule.
    /// </summary>
    public bool IsOnDemand { get; init; }
}
