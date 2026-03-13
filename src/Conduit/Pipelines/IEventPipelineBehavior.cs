using Conduit.Messages;

namespace Conduit.Pipelines;

/// <summary>
/// Wraps the event-listener pipeline, enabling cross-cutting concerns
/// such as logging or error handling around event delivery.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IEventPipelineBehavior<TEvent> where TEvent : IEvent
{
    /// <summary>
    /// Executes the behavior. Call <paramref name="next"/> to invoke the next
    /// behavior in the pipeline or the listener itself.
    /// </summary>
    /// <param name="event">The published event.</param>
    /// <param name="next">A delegate that invokes the next behavior or the listener.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task HandleAsync(
        TEvent @event,
        Func<Task> next,
        CancellationToken cancellationToken);
}
