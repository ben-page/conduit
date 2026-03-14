using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.OrderService.Events;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
