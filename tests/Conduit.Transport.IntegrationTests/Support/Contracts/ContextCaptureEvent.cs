using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record ContextCaptureEvent(string Value) : IEvent;
