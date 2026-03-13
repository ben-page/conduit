using System.Text.Json;
using Conduit.Routing;
using Conduit.Exceptions;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.AspNetCore;

/// <summary>
/// SignalR hub that accepts dispatch requests from connected clients and routes
/// them through the <see cref="IMessageRouter"/>.
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
        var dispatcher = this.Context.GetHttpContext()!.RequestServices.GetRequiredService<IMessageRouter>();

        try
        {
            var result = await dispatcher.DispatchAsync(type, payload, cancellationToken);
            return new DispatchResult<object> { Data = result };
        }
        catch (ConduitException ex)
        {
            return new DispatchResult<object>
            {
                Error = new DispatchError(ex.Code, ex.Message)
            };
        }

#pragma warning disable CA1031 // Intentional catch-all at transport boundary
        catch (Exception)
#pragma warning restore CA1031
        {
            return new DispatchResult<object>
            {
                Error = new DispatchError("InternalError", "An unexpected error occurred")
            };
        }
    }
}

// Extension to resolve services - avoids importing the DI namespace in Hub
file static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider serviceProvider) where T : notnull
        => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<T>(serviceProvider);
}
