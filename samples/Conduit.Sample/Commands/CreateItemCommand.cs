using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Commands;

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;
