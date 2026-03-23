using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record ContextCaptureEvent(string Value) : IEvent;
