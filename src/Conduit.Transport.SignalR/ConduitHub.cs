using System.Text.Json;
using Conduit.Dispatch;
using Conduit.Exceptions;
using Microsoft.AspNetCore.SignalR;

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
        var interceptors = services.GetServices<IDispatchInterceptor>();
        var router = services.GetRequiredService<IDispatchRouter>();
        var envelope = new DispatchEnvelope(type, payload);

        Func<Task<object?>> chain = () =>
            router.DispatchRequestAsync(type, payload, cancellationToken);

        foreach (var interceptor in interceptors.Reverse())
        {
            var next = chain;
            chain = () => interceptor.InterceptRequestAsync(envelope, next, cancellationToken);
        }

        try
        {
            var result = await chain();
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
        var interceptors = services.GetServices<IDispatchInterceptor>();
        var router = services.GetRequiredService<IDispatchRouter>();
        var envelope = new DispatchEnvelope(type, payload);

        Func<Task> chain = () =>
            router.DispatchEventAsync(type, payload, cancellationToken);

        foreach (var interceptor in interceptors.Reverse())
        {
            var next = chain;
            chain = () => interceptor.InterceptEventAsync(envelope, next, cancellationToken);
        }

        try
        {
            await chain();
        }

#pragma warning disable CA1031 // Intentional catch-all at transport boundary
        catch (Exception)
#pragma warning restore CA1031
        {
            // Events are fire-and-forget; errors are logged by SafeExecute in the mediator
        }
    }
}

// Extension to resolve services - avoids importing the DI namespace in Hub
file static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider serviceProvider) where T : notnull
        => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<T>(serviceProvider);

    public static IEnumerable<T> GetServices<T>(this IServiceProvider serviceProvider)
        => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetServices<T>(serviceProvider);
}
