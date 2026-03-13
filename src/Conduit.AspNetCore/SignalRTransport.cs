using Conduit.Messages;
using Conduit.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.AspNetCore;

/// <summary>
/// Bidirectional SignalR transport for Conduit messaging.
/// Delivers published events to connected SignalR clients via the <see cref="ConduitHub"/>.
/// The event type name is used as the SignalR method name.
/// </summary>
public class SignalRTransport(IHubContext<ConduitHub> hubContext) : IConduitTransport
{
    /// <inheritdoc />
    public Task DeliverAsync(IEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var eventName = @event.GetType().Name;
        return hubContext.Clients.All.SendAsync(eventName, @event, cancellationToken);
    }

    /// <inheritdoc />
    public Task<object?> SendAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "SignalR outbound send is not yet implemented. " +
            "Configure an HTTP transport for request/response messaging.");
    }
}
