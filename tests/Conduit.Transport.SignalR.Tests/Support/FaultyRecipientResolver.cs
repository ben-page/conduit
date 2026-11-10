namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>A resolver with a bug: it throws for an <see cref="ItemAssignedEvent"/> and sends anything else to everyone.</summary>
public sealed class FaultyRecipientResolver : IEventRecipientResolver
{
    public ValueTask<EventRecipients> ResolveAsync(string eventType, object @event, CancellationToken cancellationToken)
    {
        if (@event is ItemAssignedEvent)
        {
            throw new InvalidOperationException("The resolver cannot place an assignment.");
        }

        return ValueTask.FromResult(EventRecipients.All);
    }
}
