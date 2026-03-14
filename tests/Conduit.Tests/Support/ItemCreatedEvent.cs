using Conduit.Messages;

namespace Conduit.Tests.Support;

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
