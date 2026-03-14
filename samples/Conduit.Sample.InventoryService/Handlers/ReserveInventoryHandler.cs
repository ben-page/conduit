using Conduit.Handlers;
using Conduit.Sample.InventoryService.Commands;

namespace Conduit.Sample.InventoryService.Handlers;

internal sealed class ReserveInventoryHandler(InventoryStore store) : ICommandHandler<ReserveInventoryCommand, bool>
{
    public Task<bool> HandleAsync(ReserveInventoryCommand command, CancellationToken cancellationToken)
    {
        var result = store.TryReserve(command.ProductId, command.Quantity);
        return Task.FromResult(result);
    }
}
