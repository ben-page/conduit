using Conduit.Messages;

namespace Conduit.Sample.Inventory.Queries;

public record CheckStockQuery(string ProductId) : IQuery<int>;
