using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
