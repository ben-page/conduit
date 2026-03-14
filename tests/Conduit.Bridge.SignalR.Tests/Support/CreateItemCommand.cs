using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Bridge.SignalR.Tests.Support;

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;
