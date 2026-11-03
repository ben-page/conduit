using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Transport.SignalR.Tests.Support;

public class ShippedOrderHandler : ICommandHandler<ShippedOrderCommand>
{
    public Task HandleAsync(ShippedOrderCommand command, CancellationToken cancellationToken) =>
        throw new ConflictException("Order was already shipped") { Text = new("orders.shipped") };
}
