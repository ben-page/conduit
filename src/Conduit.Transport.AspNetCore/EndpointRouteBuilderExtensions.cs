using Conduit.Dispatch;
using Conduit.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Conduit.Transport.AspNetCore;

/// <summary>
/// Extension methods for mapping Conduit HTTP dispatch endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a POST endpoint that accepts <see cref="DispatchEnvelope"/> payloads
    /// and routes them through the <see cref="IDispatchRouter"/>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The URL path for the dispatch endpoint. Defaults to "/api/dispatch".</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapConduit(
        this IEndpointRouteBuilder endpoints,
        string path = "/api/dispatch")
    {
        endpoints.MapPost(path, async (
            DispatchEnvelope envelope,
            IDispatchRouter router,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await router.DispatchRequestAsync(
                    envelope.Type, envelope.Payload, cancellationToken);
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
            IDispatchRouter router,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await router.DispatchEventAsync(
                    envelope.Type, envelope.Payload, cancellationToken);
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
