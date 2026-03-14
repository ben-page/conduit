using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

public record CreateItemCommand(string Name) : ICommand<Guid>;
