using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
