using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

[Dispatchable]
public record ExplodingCommand() : ICommand;
