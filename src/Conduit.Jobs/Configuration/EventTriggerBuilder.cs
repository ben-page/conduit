using System.Globalization;
using Conduit.Jobs.Runtime;
using Conduit.Messages;

namespace Conduit.Jobs.Configuration;

/// <summary>Fluent declaration of one event-triggered job.</summary>
/// <typeparam name="TEvent">The event type the trigger reacts to.</typeparam>
public sealed class EventTriggerBuilder<TEvent>
    where TEvent : IEvent
{
    internal EventTriggerBuilder(string name, Action<JobTrigger> onBuilt)
    {
        this.Name = name;
        this.OnBuilt = onBuilt;
    }

    internal string Name { get; }

    internal Action<JobTrigger> OnBuilt { get; }

    internal TimeSpan? Delay { get; private set; }

    internal TimeSpan? DeclaredMaxRuntime { get; private set; }

    /// <summary>Delays the job until this long after the event was received.</summary>
    /// <param name="value">How long to wait before the job becomes due.</param>
    /// <returns>This builder for chaining.</returns>
    public EventTriggerBuilder<TEvent> After(TimeSpan value)
    {
        this.Delay = value;
        return this;
    }

    /// <summary>
    /// Cancels a run that exceeds this duration, marking it cancelled rather than failed. Checked when the
    /// heartbeat renews the run's claim, so it takes effect at the first renewal at or after the limit.
    /// </summary>
    /// <param name="value">The maximum duration of a single run. Must be positive.</param>
    /// <returns>This builder for chaining.</returns>
    public EventTriggerBuilder<TEvent> MaxRuntime(TimeSpan value)
    {
        this.DeclaredMaxRuntime = value;
        return this;
    }

    /// <summary>Declares the command to dispatch, completing the trigger.</summary>
    /// <remarks>
    /// Generic so the command type is inferred from the lambda and checked here: a <c>[FireAndForget]</c>
    /// command is refused, as on a schedule, and so is one the factory builds behind a wider declared type.
    /// </remarks>
    /// <typeparam name="TCommand">The command type, inferred from <paramref name="createCommand"/>.</typeparam>
    /// <param name="createCommand">Builds the command from the triggering event.</param>
    /// <exception cref="InvalidOperationException">The command is <c>[FireAndForget]</c>.</exception>
    public void Run<TCommand>(Func<TEvent, TCommand> createCommand)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(createCommand);

        var owner = $"Event trigger '{this.Name}'";
        JobRunner.RequireInlineBody(typeof(TCommand), owner);

        this.OnBuilt(new JobTrigger(
            this.Name,
            typeof(TEvent),
            this.Delay,
            this.DeclaredMaxRuntime,
            instance => JobRunner.RequireInlineBody(createCommand((TEvent)instance), owner)));
    }

    /// <summary>
    /// Coalesces this trigger's work by a key derived from the event. Events sharing a key merge into
    /// one pending unit of work instead of materializing a run each.
    /// </summary>
    /// <typeparam name="TKey">The key's type; formatted with the invariant culture.</typeparam>
    /// <param name="selector">Derives the coalesce key from the event.</param>
    /// <returns>A builder whose <c>Run</c> completes the coalescing declaration.</returns>
    public CoalescingTriggerBuilder<TEvent> CoalesceBy<TKey>(Func<TEvent, TKey> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new CoalescingTriggerBuilder<TEvent>(this, instance => FormatKey(selector((TEvent)instance)));
    }

    /// <summary>
    /// Formats a coalesce key. Invariant culture, because two hosts formatting one key differently
    /// stop agreeing on one scope and coalescing silently stops coalescing.
    /// </summary>
    private static string FormatKey<TKey>(TKey key)
    {
        var text = key is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : key?.ToString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "An event produced an empty coalesce key. Every scope for the trigger would collapse " +
                "into one, merging unrelated work. Ensure the CoalesceBy selector never returns null " +
                "or whitespace.");
        }

        return text;
    }
}
