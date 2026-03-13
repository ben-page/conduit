using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Events;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
