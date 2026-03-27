using System.Collections.Concurrent;

namespace Conduit.Sample.Inventory;

/// <summary>
/// Thread-safe in-memory inventory. Pre-populated with sample products.
/// </summary>
public sealed class InventoryStore
{
    private readonly ConcurrentDictionary<string, int> stock = new()
    {
        ["WIDGET-001"] = 100,
        ["GADGET-002"] = 50,
    };

    /// <summary>Returns the current stock level for a product.</summary>
    /// <param name="productId">The product identifier.</param>
    public int GetStock(string productId) =>
        this.stock.TryGetValue(productId, out var qty) ? qty : 0;

    /// <summary>Attempts to reserve the specified quantity, returning true on success.</summary>
    /// <param name="productId">The product identifier.</param>
    /// <param name="quantity">The quantity to reserve.</param>
    public bool TryReserve(string productId, int quantity)
    {
        while (true)
        {
            if (!this.stock.TryGetValue(productId, out var current))
            {
                return false;
            }

            if (current < quantity)
            {
                return false;
            }

            if (this.stock.TryUpdate(productId, current - quantity, current))
            {
                return true;
            }
        }
    }
}
