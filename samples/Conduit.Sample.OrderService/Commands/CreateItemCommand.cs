using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.OrderService.Commands;

[ClientVisible]
public record CreateItemCommand(string Name) : ICommand<Guid>;
