using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

public record DeleteItemCommand(Guid Id) : ICommand;
