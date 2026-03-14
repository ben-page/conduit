using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

public record ExplodingEvent(string Data) : IEvent;
