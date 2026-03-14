using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.OrderService.Commands;

[Dispatchable]
public record DeleteItemCommand(Guid Id) : ICommand;
