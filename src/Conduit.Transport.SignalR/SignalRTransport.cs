using Conduit.Dispatch;
using Conduit.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Bidirectional SignalR transport for Conduit messaging.
/// Delivers published events to connected SignalR clients via the <see cref="ConduitHub"/>.
/// The event type name is used as the SignalR method name.
/// </summary>
public class SignalRTransport(IHubContext<ConduitHub> hubContext) : IConduitTransport
{
    /// <inheritdoc />
    public Task SendMessageAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return hubContext.Clients.All.SendAsync(envelope.Type, envelope.Payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "SignalR outbound send is not yet implemented. " +
            "Configure an HTTP transport for request/response messaging.");
    }
}
