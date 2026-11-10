using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>An event for a team: <see cref="ItemRecipientResolver"/> sends it to the group named <see cref="Team"/> only.</summary>
[ClientVisible]
public record ItemSharedEvent(Guid Id, string Team) : IEvent;
