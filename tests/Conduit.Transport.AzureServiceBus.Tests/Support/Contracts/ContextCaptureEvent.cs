using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record ContextCaptureEvent(string Value) : IEvent;
