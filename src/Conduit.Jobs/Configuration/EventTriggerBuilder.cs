using Conduit.Jobs.Runtime;
using Conduit.Messages;

namespace Conduit.Jobs.Configuration;

/// <summary>Fluent declaration of one event-triggered job.</summary>
/// <typeparam name="TEvent">The event type the trigger reacts to.</typeparam>
public sealed class EventTriggerBuilder<TEvent>
    where TEvent : IEvent
{
    private readonly string name;
    private readonly Action<JobTrigger> onBuilt;
    private TimeSpan? delay;
    private TimeSpan? maxRuntime;

    internal EventTriggerBuilder(string name, Action<JobTrigger> onBuilt)
    {
        this.name = name;
        this.onBuilt = onBuilt;
    }

    /// <summary>Delays the job until this long after the event was received.</summary>
    /// <param name="value">How long to wait before the job becomes due.</param>
    /// <returns>This builder for chaining.</returns>
    public EventTriggerBuilder<TEvent> After(TimeSpan value)
    {
        this.delay = value;
        return this;
    }

    /// <summary>Cancels a run that exceeds this duration, marking it cancelled rather than failed.</summary>
    /// <param name="value">The maximum duration of a single run.</param>
    /// <returns>This builder for chaining.</returns>
    public EventTriggerBuilder<TEvent> MaxRuntime(TimeSpan value)
    {
        this.maxRuntime = value;
        return this;
    }

    /// <summary>Declares the command to dispatch, completing the trigger.</summary>
    /// <param name="createCommand">Builds the command from the triggering event.</param>
    public void Run(Func<TEvent, ICommand> createCommand)
    {
        ArgumentNullException.ThrowIfNull(createCommand);

        this.onBuilt(new JobTrigger(
            this.name,
            typeof(TEvent),
            this.delay,
            this.maxRuntime,
            instance => createCommand((TEvent)instance)));
    }
}
