using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>An event that is not <c>[ClientVisible]</c>: a server-mode hub pushes it to nobody by default.</summary>
public record ItemArchivedEvent(Guid Id) : IEvent;
