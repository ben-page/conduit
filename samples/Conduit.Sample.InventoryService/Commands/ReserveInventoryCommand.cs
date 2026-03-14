using Conduit.Messages;

namespace Conduit.Sample.InventoryService.Commands;

public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
