using Conduit.Attributes;

namespace Conduit.Sample.Commands;

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;
