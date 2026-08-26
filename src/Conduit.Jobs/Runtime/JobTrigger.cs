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
    /// <param name="coalesceKey">
    /// Formats the event's coalesce key, or <see langword="null"/> on a non-coalescing trigger.
    /// </param>
    /// <param name="commandType">
    /// The declared command type, captured at declaration. Merging deserializes the stored payload as
    /// this type; it is not resolved through <see cref="Conduit.Runtime.ConduitTypeRegistry"/> because
    /// merging always happens on a host that has the declaration in hand.
    /// </param>
    public JobTrigger(
        string name,
        Type eventType,
        TimeSpan? delay,
        TimeSpan? maxRuntime,
        Func<object, ICommand> createCommand,
        Func<object, string>? coalesceKey = null,
        Type? commandType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(createCommand);

        this.Name = name;
        this.EventType = eventType;
        this.Delay = delay;
        this.MaxRuntime = maxRuntime;
        this.CreateCommand = createCommand;
        this.CoalesceKey = coalesceKey;
        this.CommandType = commandType;
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

    /// <summary>
    /// Formats the coalesce key for an event instance, or <see langword="null"/> when this trigger
    /// does not coalesce. A coalescing trigger keys its scope <c>{Name}@{coalesceKey}</c>.
    /// </summary>
    public Func<object, string>? CoalesceKey { get; }

    /// <summary>The declared command type, used to deserialize a stored scope payload before merging.</summary>
    public Type? CommandType { get; }

    /// <summary>
    /// Combines the pending command with an incoming one. Set by the fluent builder immediately after
    /// registration; <c>AddJobs</c> rejects a coalescing trigger that still has none.
    /// </summary>
    public Func<object, object, object>? Merge { get; internal set; }
}
