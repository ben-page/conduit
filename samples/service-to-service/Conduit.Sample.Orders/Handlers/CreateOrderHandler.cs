using Conduit.Handlers;
using Conduit.Sample.Inventory.Commands;
using Conduit.Sample.Orders.Commands;

namespace Conduit.Sample.Orders.Handlers;

/// <summary>
/// Handles order creation. Reserves inventory on the remote InventoryService
/// via the routed SignalR transport before confirming the order.
/// </summary>
public sealed class CreateOrderHandler(IMediator mediator)
    : ICommandHandler<CreateOrderCommand, OrderResult>
{
    /// <inheritdoc />
    public async Task<OrderResult> HandleAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // This call routes to InventoryService via SignalR
        var reserved = await mediator.ExecuteAsync(
            new ReserveInventoryCommand(command.ProductId, command.Quantity),
            cancellationToken);

        return new OrderResult(Guid.NewGuid(), reserved);
    }
}
