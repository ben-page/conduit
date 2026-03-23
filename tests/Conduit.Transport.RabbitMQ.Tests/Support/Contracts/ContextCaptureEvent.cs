using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record ContextCaptureEvent(string Value) : IEvent;
