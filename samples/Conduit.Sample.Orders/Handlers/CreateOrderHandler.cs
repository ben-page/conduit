using System.Text.Json;
using Conduit.Configuration;
using Conduit.Dispatch;
using Conduit.Handlers;
using Conduit.Sample.Orders.Commands;

namespace Conduit.Sample.Orders.Handlers;

/// <summary>
/// Handles order creation. Reserves inventory on the remote InventoryService
/// by dispatching through the route table. The inventory types are owned by
/// InventoryService — OrderService only needs the type name and payload shape.
/// </summary>
internal sealed class CreateOrderHandler(ConduitOptions options)
    : ICommandHandler<CreateOrderCommand, OrderResult>
{
    public async Task<OrderResult> HandleAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        var endpoint = ConduitRuntime.ResolveRoute(
            options,
            "Conduit.Sample.Inventory.Commands.ReserveInventoryCommand");

        if (endpoint?.Transport is null)
        {
            throw new InvalidOperationException(
                "No route configured for Conduit.Sample.Inventory. " +
                "Ensure the inventory endpoint is configured.");
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            command.ProductId,
            command.Quantity,
        });

        var result = await endpoint.Transport.SendRequestAsync(
            new DispatchEnvelope(
                "Conduit.Sample.Inventory.Commands.ReserveInventoryCommand",
                payload),
            cancellationToken);

        var reserved = result is JsonElement je && je.GetBoolean();
        return new OrderResult(Guid.NewGuid(), reserved);
    }
}
