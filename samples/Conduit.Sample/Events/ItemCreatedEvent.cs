using Conduit;
using Conduit.Attributes;

namespace Conduit.Sample.Events;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
