using Conduit.Attributes;
using Conduit.Messages;

[assembly: DispatchableNamespace("Conduit.Tests.TestApp")]

namespace Conduit.Tests.Support;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;
