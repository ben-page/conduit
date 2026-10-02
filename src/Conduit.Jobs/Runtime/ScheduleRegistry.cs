namespace Conduit.Jobs.Runtime;

/// <summary>Singleton holding every code-declared schedule and trigger.</summary>
public sealed class ScheduleRegistry
{
    /// <summary>Initializes the registry.</summary>
    /// <param name="schedules">All declared recurring schedules.</param>
    /// <param name="triggers">All declared event triggers.</param>
    public ScheduleRegistry(IReadOnlyList<JobSchedule> schedules, IReadOnlyList<JobTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(triggers);

        this.Schedules = schedules;
        this.Triggers = triggers;
    }

    /// <summary>All declared recurring schedules.</summary>
    public IReadOnlyList<JobSchedule> Schedules { get; }

    /// <summary>All declared event triggers.</summary>
    public IReadOnlyList<JobTrigger> Triggers { get; }

    /// <summary>
    /// The declared <c>MaxRuntime</c> for a run's declaring schedule or trigger. The host that claims
    /// a run is not necessarily the one that wrote it, so the limit is looked up from the declaration
    /// rather than carried on the row.
    /// </summary>
    /// <param name="declaringName">The run's schedule or trigger name.</param>
    /// <returns>The limit, or <see langword="null"/> when none was declared.</returns>
    public TimeSpan? MaxRuntimeFor(string? declaringName)
    {
        if (declaringName is null)
        {
            return null;
        }

        foreach (var schedule in this.Schedules)
        {
            if (string.Equals(schedule.Name, declaringName, StringComparison.Ordinal))
            {
                return schedule.MaxRuntime;
            }
        }

        foreach (var trigger in this.Triggers)
        {
            if (string.Equals(trigger.Name, declaringName, StringComparison.Ordinal))
            {
                return trigger.MaxRuntime;
            }
        }

        return null;
    }

    /// <summary>Whether a run's declaring name refers to a recurring schedule rather than a trigger.</summary>
    /// <param name="declaringName">The run's schedule or trigger name.</param>
    /// <returns><see langword="true"/> when a schedule of that name is declared.</returns>
    public bool IsSchedule(string? declaringName)
        => declaringName is not null
            && this.Schedules.Any(schedule =>
                string.Equals(schedule.Name, declaringName, StringComparison.Ordinal));

    /// <summary>
    /// Whether a run's declaring name refers to a trigger keyed on the event's message id — one that
    /// does not coalesce. Only those runs keep their row on success: a schedule run has its cursor,
    /// and a coalescing run's key is the coalesce key, which must be freed for the next scope.
    /// </summary>
    /// <param name="declaringName">The run's schedule or trigger name.</param>
    /// <returns><see langword="true"/> when a non-coalescing trigger of that name is declared.</returns>
    public bool IsMessageKeyedTrigger(string? declaringName)
        => declaringName is not null
            && this.Triggers.Any(trigger =>
                trigger.CoalesceKey is null
                && string.Equals(trigger.Name, declaringName, StringComparison.Ordinal));
}
