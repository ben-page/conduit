using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Commands;

[Dispatchable]
public record DeleteItemCommand(Guid Id) : ICommand;
