using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
