using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record ThrowingTestEvent(string Value) : IEvent;
