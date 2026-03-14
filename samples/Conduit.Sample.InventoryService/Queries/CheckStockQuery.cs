using Conduit.Messages;

namespace Conduit.Sample.InventoryService.Queries;

public record CheckStockQuery(string ProductId) : IQuery<int>;
