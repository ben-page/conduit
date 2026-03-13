namespace Conduit;

public interface IEventTransport
{
    Task DeliverAsync(IEvent @event, CancellationToken cancellationToken);
}
