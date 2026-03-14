using Conduit.Handlers;
using Conduit.Sample.InventoryService.Queries;

namespace Conduit.Sample.InventoryService.Handlers;

internal sealed class CheckStockHandler(InventoryStore store) : IQueryHandler<CheckStockQuery, int>
{
    public Task<int> HandleAsync(CheckStockQuery query, CancellationToken cancellationToken)
    {
        var quantity = store.GetStock(query.ProductId);
        return Task.FromResult(quantity);
    }
}
