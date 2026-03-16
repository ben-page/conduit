using Conduit.Messages;

namespace Conduit.Tests.Support;

public record ItemDeletedEvent(Guid Id) : IEvent;
