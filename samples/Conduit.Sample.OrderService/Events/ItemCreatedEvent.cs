using Conduit.Messages;

namespace Conduit.Sample.OrderService.Events;

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
