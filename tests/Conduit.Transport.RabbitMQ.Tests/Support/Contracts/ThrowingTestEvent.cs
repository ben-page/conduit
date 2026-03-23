using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record ThrowingTestEvent(string Value) : IEvent;
