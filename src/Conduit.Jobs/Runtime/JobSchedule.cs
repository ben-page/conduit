using Conduit.Jobs.Configuration;
using Conduit.Messages;
using Cronos;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// An immutable, code-declared recurring schedule. Occurrence computation is deterministic, so every
/// host reaches the same answer without coordinating — the database decides only who runs an
/// occurrence, never when it is due.
/// </summary>
public sealed class JobSchedule
{
    private readonly CronExpression? cron;
    private readonly TimeSpan? interval;

    private JobSchedule(
        string name,
        CronExpression? cron,
        TimeSpan? interval,
        TimeZoneInfo timeZone,
        MisfirePolicy misfire,
        bool allowsOverlap,
        TimeSpan? maxRuntime,
        Func<ICommand> createCommand,
        bool runsAtStart)
    {
        this.Name = name;
        this.cron = cron;
        this.interval = interval;
        this.TimeZone = timeZone;
        this.Misfire = misfire;
        this.AllowsOverlap = allowsOverlap;
        this.MaxRuntime = maxRuntime;
        this.CreateCommand = createCommand;
        this.RunsAtStart = runsAtStart;
    }

    /// <summary>The schedule's unique name, used as its database key.</summary>
    public string Name { get; }

    /// <summary>The timezone cron expressions are evaluated in. UTC unless overridden.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>What to do about occurrences missed while the application was down.</summary>
    public MisfirePolicy Misfire { get; }

    /// <summary>Whether a new occurrence may start while a previous one is outstanding.</summary>
    public bool AllowsOverlap { get; }

    /// <summary>Optional ceiling on a single run's duration before it is cancelled.</summary>
    public TimeSpan? MaxRuntime { get; }

    /// <summary>Builds the command dispatched for one occurrence.</summary>
    public Func<ICommand> CreateCommand { get; }

    /// <summary>
    /// Whether the schedule runs once when its host's scheduler starts, besides its timetable. The run
    /// is not an occurrence: it never moves the cursor, and the timetable's phase is unchanged. Only an
    /// interval schedule can carry it.
    /// </summary>
    public bool RunsAtStart { get; }

    /// <summary>Creates a cron-driven schedule.</summary>
    /// <param name="name">The schedule's unique name.</param>
    /// <param name="cronExpression">Five cron fields, or six to include seconds.</param>
    /// <param name="timeZone">The timezone the expression is evaluated in.</param>
    /// <param name="misfire">What to do about occurrences missed while the application was down.</param>
    /// <param name="allowsOverlap">Whether a new occurrence may start while a previous one is outstanding.</param>
    /// <param name="maxRuntime">Optional ceiling on a single run's duration.</param>
    /// <param name="createCommand">Builds the command dispatched for one occurrence.</param>
    /// <returns>The declared schedule.</returns>
    public static JobSchedule FromCron(
        string name,
        string cronExpression,
        TimeZoneInfo timeZone,
        MisfirePolicy misfire,
        bool allowsOverlap,
        TimeSpan? maxRuntime,
        Func<ICommand> createCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(createCommand);

        var fields = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        return new JobSchedule(
            name, CronExpression.Parse(cronExpression, format), interval: null,
            timeZone, misfire, allowsOverlap, maxRuntime, createCommand, runsAtStart: false);
    }

    /// <summary>Creates a fixed-interval schedule. Interval schedules have no misfire policy.</summary>
    /// <param name="name">The schedule's unique name.</param>
    /// <param name="interval">The gap between occurrences. Must be greater than zero.</param>
    /// <param name="allowsOverlap">Whether a new occurrence may start while a previous one is outstanding.</param>
    /// <param name="maxRuntime">Optional ceiling on a single run's duration.</param>
    /// <param name="createCommand">Builds the command dispatched for one occurrence.</param>
    /// <param name="runsAtStart">Whether the schedule also runs once when its host's scheduler starts.</param>
    /// <returns>The declared schedule.</returns>
    public static JobSchedule FromInterval(
        string name,
        TimeSpan interval,
        bool allowsOverlap,
        TimeSpan? maxRuntime,
        Func<ICommand> createCommand,
        bool runsAtStart = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(createCommand);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), interval, "Schedule interval must be greater than zero.");
        }

        return new JobSchedule(
            name, cron: null, interval, TimeZoneInfo.Utc,
            MisfirePolicy.Skip, allowsOverlap, maxRuntime, createCommand, runsAtStart);
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>, or <see langword="null"/> if the
    /// expression yields none. Cronos resolves DST transitions: an occurrence in a skipped local hour
    /// is not produced, and one in a repeated local hour is produced once.
    /// </summary>
    /// <param name="after">The instant to search forward from, exclusive.</param>
    /// <returns>The next occurrence, or <see langword="null"/> when the expression yields none.</returns>
    public DateTimeOffset? NextOccurrenceAfter(DateTimeOffset after)
    {
        if (this.interval is { } every)
        {
            return after + every;
        }

        return this.cron!.GetNextOccurrence(after, this.TimeZone, inclusive: false);
    }
}
