using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record ContextCaptureEvent(string Value) : IEvent;
