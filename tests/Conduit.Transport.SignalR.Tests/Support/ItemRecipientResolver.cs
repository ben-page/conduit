using System.Collections.Concurrent;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// Scopes the item events by their typed payload: an assignment to its user, a share to its team's group, and an archive
/// (not <c>[ClientVisible]</c>, which the default pushes to nobody) to everyone. Records every event it is asked about.
/// </summary>
public sealed class ItemRecipientResolver : IEventRecipientResolver
{
    public ConcurrentQueue<(string EventType, object Event)> Asked { get; } = new();

    public ValueTask<EventRecipients> ResolveAsync(string eventType, object @event, CancellationToken cancellationToken)
    {
        this.Asked.Enqueue((eventType, @event));
        var recipients = @event switch
        {
            ItemAssignedEvent assigned => EventRecipients.Users([assigned.UserId]),
            ItemSharedEvent shared => EventRecipients.Groups([shared.Team]),
            ItemArchivedEvent => EventRecipients.All,
            _ => EventRecipients.None,
        };

        return ValueTask.FromResult(recipients);
    }
}
