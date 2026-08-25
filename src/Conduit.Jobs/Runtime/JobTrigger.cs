using Conduit.Messages;

namespace Conduit.Jobs.Runtime;

/// <summary>An immutable, code-declared mapping from an event type to a job command.</summary>
public sealed class JobTrigger
{
    /// <summary>Initializes a new trigger.</summary>
    /// <param name="name">The trigger's unique name, forming half of each run's database key.</param>
    /// <param name="eventType">The event type this trigger reacts to.</param>
    /// <param name="delay">Optional delay between the event and the job becoming due.</param>
    /// <param name="maxRuntime">Optional ceiling on a single run's duration.</param>
    /// <param name="createCommand">Builds the command from the triggering event instance.</param>
    public JobTrigger(
        string name,
        Type eventType,
        TimeSpan? delay,
        TimeSpan? maxRuntime,
        Func<object, ICommand> createCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(createCommand);

        this.Name = name;
        this.EventType = eventType;
        this.Delay = delay;
        this.MaxRuntime = maxRuntime;
        this.CreateCommand = createCommand;
    }

    /// <summary>The trigger's unique name, forming half of each run's database key.</summary>
    public string Name { get; }

    /// <summary>The event type this trigger reacts to.</summary>
    public Type EventType { get; }

    /// <summary>Optional delay between the event and the job becoming due.</summary>
    public TimeSpan? Delay { get; }

    /// <summary>Optional ceiling on a single run's duration before it is cancelled.</summary>
    public TimeSpan? MaxRuntime { get; }

    /// <summary>Builds the command from the triggering event instance.</summary>
    public Func<object, ICommand> CreateCommand { get; }
}
