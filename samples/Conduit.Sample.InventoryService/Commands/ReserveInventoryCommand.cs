using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.InventoryService.Commands;

[Dispatchable]
public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
