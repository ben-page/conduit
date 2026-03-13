using Conduit;
using Conduit.Attributes;

namespace Conduit.Sample.Commands;

[Dispatchable]
public record DeleteItemCommand(Guid Id) : ICommand;
