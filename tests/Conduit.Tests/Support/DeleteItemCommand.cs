using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Tests.Support;

[Dispatchable]
public record DeleteItemCommand(Guid Id) : ICommand;
