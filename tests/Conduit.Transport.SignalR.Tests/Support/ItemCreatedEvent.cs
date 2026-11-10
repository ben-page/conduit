using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
