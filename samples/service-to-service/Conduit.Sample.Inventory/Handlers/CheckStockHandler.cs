using Conduit.Handlers;
using Conduit.Sample.Inventory.Queries;

namespace Conduit.Sample.Inventory.Handlers;

internal sealed class CheckStockHandler(InventoryStore store)
    : IQueryHandler<CheckStockQuery, int>
{
    public Task<int> HandleAsync(
        CheckStockQuery query,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(store.GetStock(query.ProductId));
    }
}
