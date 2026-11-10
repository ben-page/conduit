using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>An event for one user: <see cref="ItemRecipientResolver"/> sends it to <see cref="UserId"/> only.</summary>
[ClientVisible]
public record ItemAssignedEvent(Guid Id, string UserId) : IEvent;
