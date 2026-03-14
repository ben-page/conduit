namespace Conduit.Sample.InventoryService;

/// <summary>
/// Thread-safe in-memory inventory store for the sample app.
/// </summary>
public sealed class InventoryStore
{
    private readonly Dictionary<string, int> stock = new()
    {
        ["WIDGET-001"] = 100,
        ["GADGET-002"] = 50,
    };

    /// <summary>
    /// Gets the current stock level for the specified product.
    /// </summary>
    public int GetStock(string productId) => this.stock.GetValueOrDefault(productId, 0);

    /// <summary>
    /// Attempts to reserve the specified quantity. Returns true if successful.
    /// </summary>
    public bool TryReserve(string productId, int quantity)
    {
        if (!this.stock.TryGetValue(productId, out var available) || available < quantity)
        {
            return false;
        }

        this.stock[productId] = available - quantity;
        return true;
    }
}
