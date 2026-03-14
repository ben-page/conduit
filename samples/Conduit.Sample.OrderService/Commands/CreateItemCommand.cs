using Conduit.Messages;

namespace Conduit.Sample.OrderService.Commands;

public record CreateItemCommand(string Name) : ICommand<Guid>;
