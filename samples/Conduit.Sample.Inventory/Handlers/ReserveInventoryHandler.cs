using Conduit.Handlers;
using Conduit.Sample.Inventory.Commands;

namespace Conduit.Sample.Inventory.Handlers;

internal sealed class ReserveInventoryHandler(InventoryStore store)
    : ICommandHandler<ReserveInventoryCommand, bool>
{
    public Task<bool> HandleAsync(
        ReserveInventoryCommand command,
        CancellationToken cancellationToken)
    {
        var success = store.TryReserve(command.ProductId, command.Quantity);
        return Task.FromResult(success);
    }
}
