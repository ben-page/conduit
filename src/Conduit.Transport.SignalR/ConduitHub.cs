using System.Text.Json;
using Conduit.Dispatch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.SignalR;

/// <summary>
/// SignalR hub that accepts dispatch requests from connected clients (both C# services
/// and browser clients) and routes them through the <see cref="IDispatchRouter"/>.
/// Registered <see cref="IDispatchInterceptor"/> instances are chained before the router.
/// </summary>
public class ConduitHub : Hub
{
    /// <summary>
    /// Dispatches a command or query received from a SignalR client.
    /// </summary>
    /// <param name="type">The type name of the request.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <returns>A <see cref="DispatchResult{T}"/> containing the result or error.</returns>
    public async Task<DispatchResult<object>> Dispatch(
        string type,
        JsonElement payload)
    {
        var cancellationToken = this.Context.ConnectionAborted;
        var services = this.Context.GetHttpContext()!.RequestServices;
        var envelope = new DispatchEnvelope(type, payload);
        var interceptors = services.GetServices<IDispatchInterceptor>();
        var router = services.GetRequiredService<IDispatchRouter>();
        var logger = services.GetRequiredService<ILogger<ConduitHub>>();

        return await DispatchPipeline.ExecuteRequestAsync(
            envelope, interceptors, router, logger, cancellationToken);
    }

    /// <summary>
    /// Publishes an event received from a SignalR client through the local mediator.
    /// This enables TypeScript clients to publish events to C# listeners.
    /// </summary>
    /// <param name="type">The type name of the event.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    public async Task Publish(
        string type,
        JsonElement payload)
    {
        var cancellationToken = this.Context.ConnectionAborted;
        var services = this.Context.GetHttpContext()!.RequestServices;
        var envelope = new DispatchEnvelope(type, payload);
        var interceptors = services.GetServices<IDispatchInterceptor>();
        var router = services.GetRequiredService<IDispatchRouter>();
        var logger = services.GetRequiredService<ILogger<ConduitHub>>();

        await DispatchPipeline.ExecuteEventAsync(
            envelope, interceptors, router, logger, cancellationToken);
    }
}
