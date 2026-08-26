using Conduit.Jobs.Runtime;
using Conduit.Messages;

namespace Conduit.Jobs.Configuration;

/// <summary>Fluent declaration of every schedule and event trigger in the application.</summary>
public sealed class JobsBuilder
{
    private readonly List<JobSchedule> schedules = [];
    private readonly List<JobTrigger> triggers = [];

    internal JobsBuilder()
    {
    }

    /// <summary>The tuning knobs applied to the jobs runtime.</summary>
    internal JobsOptions Options { get; } = new();

    /// <summary>Every schedule declared so far.</summary>
    internal IReadOnlyList<JobSchedule> Schedules => this.schedules;

    /// <summary>Every event trigger declared so far.</summary>
    internal IReadOnlyList<JobTrigger> Triggers => this.triggers;

    /// <inheritdoc cref="JobsOptions.HeartbeatInterval"/>
    public TimeSpan HeartbeatInterval
    {
        get => this.Options.HeartbeatInterval;
        set => this.Options.HeartbeatInterval = value;
    }

    /// <inheritdoc cref="JobsOptions.ClaimTimeout"/>
    public TimeSpan ClaimTimeout
    {
        get => this.Options.ClaimTimeout;
        set => this.Options.ClaimTimeout = value;
    }

    /// <inheritdoc cref="JobsOptions.MaxAttempts"/>
    public int MaxAttempts
    {
        get => this.Options.MaxAttempts;
        set => this.Options.MaxAttempts = value;
    }

    /// <inheritdoc cref="JobsOptions.FailedRetention"/>
    public TimeSpan FailedRetention
    {
        get => this.Options.FailedRetention;
        set => this.Options.FailedRetention = value;
    }

    /// <summary>Begins declaring a recurring schedule.</summary>
    /// <param name="name">The schedule's unique name. Used as its database key.</param>
    /// <returns>A builder for the schedule.</returns>
    public ScheduleBuilder Schedule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ScheduleBuilder(name, this.schedules.Add);
    }

    /// <summary>Begins declaring a job triggered by an event.</summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <param name="name">
    /// The trigger's unique name, forming half of each run's database key. Defaults to the event's
    /// type name, which is why two unnamed triggers on one event type collide.
    /// </param>
    /// <returns>A builder for the trigger.</returns>
    public EventTriggerBuilder<TEvent> On<TEvent>(string? name = null)
        where TEvent : IEvent
        => new(name ?? typeof(TEvent).Name, this.triggers.Add);
}
