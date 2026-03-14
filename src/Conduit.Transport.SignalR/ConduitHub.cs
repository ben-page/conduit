using System.Text.Json;
using Conduit.Dispatch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR;

/// <summary>
/// SignalR hub that accepts dispatch requests from connected clients
/// and delegates to <see cref="IWireMediator"/> for processing.
/// </summary>
public class ConduitHub : Hub
{
    /// <summary>
    /// Dispatches a command or query received from a SignalR client.
    /// </summary>
    public async Task<DispatchResult<object>> Dispatch(string type, JsonElement payload)
    {
        var mediator = this.Context.GetHttpContext()!.RequestServices
            .GetRequiredService<IWireMediator>();

        return await mediator.HandleWireRequestAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Publishes an event received from a SignalR client.
    /// </summary>
    public async Task Publish(string type, JsonElement payload)
    {
        var mediator = this.Context.GetHttpContext()!.RequestServices
            .GetRequiredService<IWireMediator>();

        await mediator.HandleWireEventAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
    }
}
