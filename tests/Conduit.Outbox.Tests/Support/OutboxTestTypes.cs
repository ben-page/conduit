using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Outbox.Tests.Support;

public record OrderPlacedEvent(Guid OrderId, string Product) : IEvent;

public record PlaceOrderCommand(string Product) : ICommand<Guid>;

public class PlaceOrderHandler(IMediator mediator) : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid();
        await mediator.PublishAsync(new OrderPlacedEvent(orderId, command.Product), cancellationToken);
        return orderId;
    }
}
