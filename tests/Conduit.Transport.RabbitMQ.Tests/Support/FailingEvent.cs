using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record FailingEvent(string Data) : IEvent;
