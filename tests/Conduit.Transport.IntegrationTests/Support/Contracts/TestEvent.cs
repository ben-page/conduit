using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record TestEvent(string Value) : IEvent;
