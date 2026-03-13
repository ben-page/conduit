using Conduit.Dispatch;
using Conduit.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
namespace Conduit.Bridge.Http;

/// <summary>
/// Extension methods for mapping browser-facing HTTP dispatch endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a POST endpoint that accepts <see cref="DispatchEnvelope"/> payloads
    /// from browser clients and routes them through the <see cref="IDispatchRouter"/>,
    /// applying any registered <see cref="IDispatchInterceptor"/> instances.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The URL path for the dispatch endpoint. Defaults to "/api/dispatch".</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapConduitBridge(
        this IEndpointRouteBuilder endpoints,
        string path = "/api/dispatch")
    {
        endpoints.MapPost(path, async (
            DispatchEnvelope envelope,
            IEnumerable<IDispatchInterceptor> interceptors,
            IDispatchRouter router,
            CancellationToken cancellationToken) =>
        {
            Func<Task<object?>> chain = () =>
                router.DispatchRequestAsync(envelope.Type, envelope.Payload, cancellationToken);

            foreach (var interceptor in interceptors.Reverse())
            {
                var next = chain;
                chain = () => interceptor.InterceptRequestAsync(envelope, next, cancellationToken);
            }

            try
            {
                var result = await chain();
                return Results.Ok(new DispatchResult<object> { Data = result });
            }
            catch (ConduitException ex)
            {
                return Results.Json(
                    new DispatchResult<object>
                    {
                        Error = new DispatchError(ex.Code, ex.Message)
                    },
                    statusCode: ex.StatusCode);
            }

#pragma warning disable CA1031 // Intentional catch-all at transport boundary
            catch (Exception)
#pragma warning restore CA1031
            {
                return Results.Json(
                    new DispatchResult<object>
                    {
                        Error = new DispatchError("InternalError", "An unexpected error occurred")
                    },
                    statusCode: 500);
            }
        });

        endpoints.MapPost(path + "/publish", async (
            DispatchEnvelope envelope,
            IEnumerable<IDispatchInterceptor> interceptors,
            IDispatchRouter router,
            CancellationToken cancellationToken) =>
        {
            Func<Task> chain = () =>
                router.DispatchEventAsync(envelope.Type, envelope.Payload, cancellationToken);

            foreach (var interceptor in interceptors.Reverse())
            {
                var next = chain;
                chain = () => interceptor.InterceptEventAsync(envelope, next, cancellationToken);
            }

            try
            {
                await chain();
                return Results.Ok();
            }

#pragma warning disable CA1031 // Intentional catch-all at transport boundary
            catch (Exception)
#pragma warning restore CA1031
            {
                return Results.Json(
                    new DispatchResult<object>
                    {
                        Error = new DispatchError("InternalError", "An unexpected error occurred")
                    },
                    statusCode: 500);
            }
        });

        return endpoints;
    }
}
