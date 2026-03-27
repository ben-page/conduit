using Conduit.Messages;

namespace Conduit.Sample.Inventory.Commands;

/// <summary>Reserves inventory for a product. Returns true if stock was available.</summary>
/// <param name="ProductId">The product to reserve.</param>
/// <param name="Quantity">The number of units to reserve.</param>
public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
