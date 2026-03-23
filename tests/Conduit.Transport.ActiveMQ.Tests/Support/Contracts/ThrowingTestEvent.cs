using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record ThrowingTestEvent(string Value) : IEvent;
