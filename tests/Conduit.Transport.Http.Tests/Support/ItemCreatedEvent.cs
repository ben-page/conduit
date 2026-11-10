using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
