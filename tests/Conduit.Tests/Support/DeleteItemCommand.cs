using Conduit.Messages;

namespace Conduit.Tests.Support;

public record DeleteItemCommand(Guid Id) : ICommand;
