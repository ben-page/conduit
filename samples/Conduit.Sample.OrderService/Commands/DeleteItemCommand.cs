using Conduit.Messages;

namespace Conduit.Sample.OrderService.Commands;

public record DeleteItemCommand(Guid Id) : ICommand;
