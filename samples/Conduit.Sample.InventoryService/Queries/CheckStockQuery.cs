using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.InventoryService.Queries;

[Dispatchable]
public record CheckStockQuery(string ProductId) : IQuery<int>;
