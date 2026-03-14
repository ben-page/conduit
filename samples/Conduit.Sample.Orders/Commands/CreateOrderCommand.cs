using Conduit.Messages;

namespace Conduit.Sample.Orders.Commands;

public record CreateOrderCommand(string ProductId, int Quantity) : ICommand<OrderResult>;

public record OrderResult(Guid OrderId, bool InventoryReserved);
