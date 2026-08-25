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
}
