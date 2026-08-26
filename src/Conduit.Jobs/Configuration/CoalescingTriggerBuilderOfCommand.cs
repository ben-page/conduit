using Conduit.Jobs.Runtime;
using Conduit.Messages;

namespace Conduit.Jobs.Configuration;

/// <summary>A registered coalescing trigger awaiting its merge function.</summary>
/// <typeparam name="TEvent">The event type the trigger reacts to.</typeparam>
/// <typeparam name="TCommand">The command type the trigger dispatches.</typeparam>
public sealed class CoalescingTriggerBuilder<TEvent, TCommand>
    where TEvent : IEvent
    where TCommand : ICommand
{
    private readonly JobTrigger trigger;

    internal CoalescingTriggerBuilder(JobTrigger trigger)
    {
        this.trigger = trigger;
    }

    /// <summary>Declares how the pending command and an incoming one combine.</summary>
    /// <param name="merge">
    /// Returns the command covering both units of work. <b>Widen, never narrow</b> — a merge that
    /// loses window is silent staleness, and doing the work twice is the cheaper mistake.
    /// </param>
    public void Merge(Func<TCommand, TCommand, TCommand> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        this.trigger.Merge = (pending, incoming) => merge((TCommand)pending, (TCommand)incoming);
    }
}
