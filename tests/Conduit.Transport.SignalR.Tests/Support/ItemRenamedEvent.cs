using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>A <c>[ClientVisible]</c> event: a server-mode hub pushes it to every connection by default.</summary>
[ClientVisible]
public record ItemRenamedEvent(Guid Id, string Name) : IEvent;
