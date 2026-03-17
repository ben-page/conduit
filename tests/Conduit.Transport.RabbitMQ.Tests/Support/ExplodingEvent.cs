using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record ExplodingEvent(string Data) : IEvent;
