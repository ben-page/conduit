using System.Text.Json;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.Transport.SignalR;

/// <summary>
/// SignalR hub that accepts dispatch requests from connected clients
/// and delegates to <see cref="IWireMediator"/> for processing.
/// </summary>
public class ConduitHub(IWireMediator mediator) : Hub
{
    /// <summary>
    /// Dispatches a command or query received from a SignalR client.
    /// </summary>
    public async Task<DispatchResult<object>> Dispatch(string type, JsonElement payload)
    {
        return await mediator.HandleWireRequestAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Publishes an event received from a SignalR client.
    /// </summary>
    public async Task Publish(string type, JsonElement payload)
    {
        await mediator.HandleWireEventAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
    }
}
