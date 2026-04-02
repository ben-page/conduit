using Conduit.Handlers;
using Conduit.Sample.Inventory.Queries;

namespace Conduit.Sample.Inventory.Handlers;

/// <inheritdoc />
public sealed class CheckStockHandler(InventoryStore store)
    : IQueryHandler<CheckStockQuery, int>
{
    /// <inheritdoc />
    public Task<int> HandleAsync(
        CheckStockQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Task.FromResult(store.GetStock(query.ProductId));
    }
}
