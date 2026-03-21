using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record ThrowingTestEvent(string Value) : IEvent;
