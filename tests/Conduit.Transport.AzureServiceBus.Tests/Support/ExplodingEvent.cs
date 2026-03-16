using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record ExplodingEvent(string Data) : IEvent;
