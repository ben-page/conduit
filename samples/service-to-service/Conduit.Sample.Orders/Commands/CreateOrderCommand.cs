using Conduit.Messages;

namespace Conduit.Sample.Orders.Commands;

/// <summary>Creates a new order for a product.</summary>
/// <param name="ProductId">The product identifier.</param>
/// <param name="Quantity">The quantity to order.</param>
public record CreateOrderCommand(string ProductId, int Quantity) : ICommand<OrderResult>;

/// <summary>The result of creating an order.</summary>
/// <param name="OrderId">The new order identifier.</param>
/// <param name="InventoryReserved">Whether inventory was successfully reserved.</param>
public record OrderResult(Guid OrderId, bool InventoryReserved);
