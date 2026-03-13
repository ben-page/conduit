namespace Conduit;

public interface IEventPipelineBehavior<TEvent> where TEvent : IEvent
{
    Task HandleAsync(
        TEvent @event,
        Func<Task> next,
        CancellationToken cancellationToken);
}
