using Conduit.Handlers;
using Conduit.Sample.Inventory.Commands;
using Conduit.Sample.Orders.Commands;

namespace Conduit.Sample.Orders.Handlers;

/// <summary>
/// Handles order creation. Reserves inventory on the remote InventoryService
/// via the routed SignalR transport before confirming the order.
/// </summary>
internal sealed class CreateOrderHandler(IMediator mediator)
    : ICommandHandler<CreateOrderCommand, OrderResult>
{
    public async Task<OrderResult> HandleAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        // This call routes to InventoryService via SignalR
        var reserved = await mediator.ExecuteAsync(
            new ReserveInventoryCommand(command.ProductId, command.Quantity),
            cancellationToken);

        return new OrderResult(Guid.NewGuid(), reserved);
    }
}
