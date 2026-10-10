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

        var schedule = this.cronExpression is not null
            ? JobSchedule.FromCron(this.name, this.cronExpression, this.timeZone, this.misfire,
                this.allowsOverlap, this.maxRuntime, createCommand)
            : JobSchedule.FromInterval(this.name, this.interval!.Value, this.allowsOverlap,
                this.maxRuntime, createCommand);

        this.onBuilt(schedule);
    }
}
