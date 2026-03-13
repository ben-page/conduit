using System.Text.Json;
using Conduit.Exceptions;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.AspNetCore;

public class ConduitHub : Hub
{
    public async Task<DispatchResult<object>> Dispatch(
        string type,
        JsonElement payload)
    {
        var cancellationToken = Context.ConnectionAborted;
        var dispatcher = Context.GetHttpContext()!.RequestServices.GetRequiredService<IWireDispatcher>();

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
        catch (Exception)
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
