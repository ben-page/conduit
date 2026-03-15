using Conduit.Messages;

namespace Conduit.Sample.Inventory.Commands;

public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
