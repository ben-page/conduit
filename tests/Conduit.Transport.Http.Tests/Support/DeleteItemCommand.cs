using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record DeleteItemCommand(Guid Id) : ICommand;
