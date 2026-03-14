using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Bridge.Http.Tests.Support;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
