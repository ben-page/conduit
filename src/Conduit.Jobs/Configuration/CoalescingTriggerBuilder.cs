using Conduit.Jobs.Runtime;
using Conduit.Messages;

namespace Conduit.Jobs.Configuration;

/// <summary>
/// A coalescing event trigger awaiting its command declaration. <c>Run</c> is generic so the command
/// type is inferred from the lambda, which is what lets <c>Merge</c> be strongly typed without the
/// caller writing a type argument.
/// </summary>
/// <typeparam name="TEvent">The event type the trigger reacts to.</typeparam>
public sealed class CoalescingTriggerBuilder<TEvent>
    where TEvent : IEvent
{
    private readonly EventTriggerBuilder<TEvent> owner;
    private readonly Func<object, string> coalesceKey;

    internal CoalescingTriggerBuilder(EventTriggerBuilder<TEvent> owner, Func<object, string> coalesceKey)
    {
        this.owner = owner;
        this.coalesceKey = coalesceKey;
    }

    /// <summary>Declares the command to dispatch, registering the trigger.</summary>
    /// <typeparam name="TCommand">The command type, inferred from <paramref name="createCommand"/>.</typeparam>
    /// <param name="createCommand">Builds the command from the triggering event.</param>
    /// <returns>A builder for declaring how two of these commands merge.</returns>
    /// <remarks>
    /// Registration happens here rather than on <c>Merge</c>: registering on <c>Merge</c> would mean a
    /// forgotten <c>Merge</c> silently drops the whole trigger, which is a worse failure than the
    /// startup exception <c>AddJobs</c> throws instead.
    /// </remarks>
    public CoalescingTriggerBuilder<TEvent, TCommand> Run<TCommand>(Func<TEvent, TCommand> createCommand)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(createCommand);

        var trigger = new JobTrigger(
            this.owner.Name,
            typeof(TEvent),
            this.owner.Delay,
            this.owner.DeclaredMaxRuntime,
            instance => createCommand((TEvent)instance),
            this.coalesceKey,
            typeof(TCommand));

        this.owner.OnBuilt(trigger);
        return new CoalescingTriggerBuilder<TEvent, TCommand>(trigger);
    }
}
