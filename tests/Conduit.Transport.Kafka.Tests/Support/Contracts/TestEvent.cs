using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record TestEvent(string Value) : IEvent;
