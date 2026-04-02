using Conduit.Handlers;
using Conduit.Sample.Inventory.Commands;

namespace Conduit.Sample.Inventory.Handlers;

/// <inheritdoc />
public sealed class ReserveInventoryHandler(InventoryStore store)
    : ICommandHandler<ReserveInventoryCommand, bool>
{
    /// <inheritdoc />
    public Task<bool> HandleAsync(
        ReserveInventoryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var success = store.TryReserve(command.ProductId, command.Quantity);
        return Task.FromResult(success);
    }
}
