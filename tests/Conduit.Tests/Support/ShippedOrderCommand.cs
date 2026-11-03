using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>A void command whose handler refuses with a named sentence, as a host's handler does.</summary>
public record ShippedOrderCommand : ICommand;

public class ShippedOrderCommandHandler : ICommandHandler<ShippedOrderCommand>
{
    public Task HandleAsync(ShippedOrderCommand command, CancellationToken cancellationToken) =>
        throw new ConflictException("Order was already shipped") { Text = new("orders.shipped") };
}
