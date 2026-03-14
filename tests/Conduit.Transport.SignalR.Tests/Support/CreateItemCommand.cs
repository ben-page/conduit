using Conduit.Attributes;
using Conduit.Messages;

[assembly: DispatchableNamespace("Conduit.Transport.SignalR.Tests.Support")]

namespace Conduit.Transport.SignalR.Tests.Support;

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;
