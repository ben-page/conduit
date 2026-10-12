using Conduit.Jobs.Runtime;
using Conduit.Messages;

namespace Conduit.Jobs.Configuration;

/// <summary>Fluent declaration of one recurring schedule.</summary>
public sealed class ScheduleBuilder
{
    private readonly string name;
    private readonly Action<JobSchedule> onBuilt;
    private string? cronExpression;
    private TimeSpan? interval;
    private TimeZoneInfo timeZone = TimeZoneInfo.Utc;
    private MisfirePolicy misfire = MisfirePolicy.Skip;
    private bool allowsOverlap;
    private TimeSpan? maxRuntime;
    private bool runsAtStart;

    internal ScheduleBuilder(string name, Action<JobSchedule> onBuilt)
    {
        this.name = name;
        this.onBuilt = onBuilt;
    }

    /// <summary>Declares a cron expression. Five fields, or six to include seconds.</summary>
    /// <param name="expression">The cron expression.</param>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder Cron(string expression)
    {
        this.cronExpression = expression;
        return this;
    }

    /// <summary>Declares a fixed interval. Interval schedules have no misfire policy.</summary>
    /// <param name="value">The gap between occurrences.</param>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder Every(TimeSpan value)
    {
        this.interval = value;
        return this;
    }

    /// <summary>Evaluates the cron expression in this timezone instead of UTC.</summary>
    /// <param name="timeZoneId">The IANA or Windows timezone identifier.</param>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder InTimeZone(string timeZoneId)
    {
        this.timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return this;
    }

    /// <summary>Chooses what happens to occurrences missed while the application was down.</summary>
    /// <param name="policy">The misfire policy.</param>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder OnMisfire(MisfirePolicy policy)
    {
        this.misfire = policy;
        return this;
    }

    /// <summary>
    /// Allows a new run — an occurrence, or a manual <see cref="IJobs.TriggerNowAsync"/> — to start
    /// while another run of the schedule is still outstanding. Without it the occurrence is skipped and
    /// the manual run refused with <see cref="JobOverlapException"/>.
    /// </summary>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder AllowOverlap()
    {
        this.allowsOverlap = true;
        return this;
    }

    /// <summary>Cancels a run that exceeds this duration, marking it cancelled rather than failed.</summary>
    /// <param name="value">The maximum duration of a single run.</param>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder MaxRuntime(TimeSpan value)
    {
        this.maxRuntime = value;
        return this;
    }

    /// <summary>
    /// Runs the schedule once when its host's scheduler starts, as well as on its timetable. For
    /// <see cref="Every"/> schedules only; a cron schedule catches a missed occurrence up with
    /// <see cref="OnMisfire"/>(<see cref="MisfirePolicy.RunImmediately"/>) instead.
    /// </summary>
    /// <remarks>
    /// The start-up run is not an occurrence. It is written the way <see cref="IJobs.TriggerNowAsync"/>
    /// writes a manual run — no occurrence, through the same overlap lock unless <see cref="AllowOverlap"/>
    /// is declared (then through its start-instant key alone), keyed by the start instant —
    /// so it never moves the schedule's cursor and the timetable's phase is unchanged: the next
    /// occurrence may follow it by less than an interval. Each host's start writes its own start-up
    /// run; without <see cref="AllowOverlap"/> the overlap lock keeps at most one run outstanding across
    /// hosts, so a run still outstanding at start (a retry left from before a restart, a manual run,
    /// another replica's start-up run) stands in for it. A paused schedule gets none.
    /// </remarks>
    /// <returns>This builder for chaining.</returns>
    public ScheduleBuilder RunAtStart()
    {
        this.runsAtStart = true;
        return this;
    }

    /// <summary>Declares the command to dispatch, completing the schedule.</summary>
    /// <param name="createCommand">Builds the command dispatched for one occurrence.</param>
    public void Run(Func<ICommand> createCommand)
    {
        ArgumentNullException.ThrowIfNull(createCommand);

        if (this.cronExpression is null && this.interval is null)
        {
            throw new InvalidOperationException(
                $"Job schedule '{this.name}' declares neither Cron(...) nor Every(...).");
        }

        if (this.cronExpression is not null && this.interval is not null)
        {
            throw new InvalidOperationException(
                $"Job schedule '{this.name}' declares both Cron(...) and Every(...). Choose one.");
        }

        if (this.runsAtStart && this.cronExpression is not null)
        {
            throw new InvalidOperationException(
                $"Job schedule '{this.name}' declares RunAtStart() on a Cron(...) schedule. RunAtStart() is for Every(...) " +
                "schedules; a cron schedule catches a missed occurrence up with OnMisfire(MisfirePolicy.RunImmediately).");
        }

        var schedule = this.cronExpression is not null
            ? JobSchedule.FromCron(this.name, this.cronExpression, this.timeZone, this.misfire,
                this.allowsOverlap, this.maxRuntime, createCommand)
            : JobSchedule.FromInterval(this.name, this.interval!.Value, this.allowsOverlap,
                this.maxRuntime, createCommand, this.runsAtStart);

        this.onBuilt(schedule);
    }
}
