using Conduit.Messages;

namespace Conduit.Sample.Inventory.Queries;

/// <summary>Checks the current stock level for a product.</summary>
/// <param name="ProductId">The product to check.</param>
public record CheckStockQuery(string ProductId) : IQuery<int>;
