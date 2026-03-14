using Conduit.Dispatch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
namespace Conduit.Transport.Http;

/// <summary>
/// Extension methods for mapping browser-facing HTTP dispatch endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a POST endpoint that accepts <see cref="DispatchEnvelope"/> payloads
    /// and dispatches them through the <see cref="IWireDispatcher"/>,
    /// applying any registered <see cref="IDispatchInterceptor"/> instances.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The URL path for the dispatch endpoint. Defaults to "/api/dispatch".</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapConduitEndpoint(
        this IEndpointRouteBuilder endpoints,
        string path = "/api/dispatch")
    {
        endpoints.MapPost(path, async (
            DispatchEnvelope envelope,
            IEnumerable<IDispatchInterceptor> interceptors,
            IWireDispatcher wireDispatcher,
            ILogger<IWireDispatcher> logger,
            CancellationToken cancellationToken) =>
        {
            var result = await DispatchPipeline.ExecuteRequestAsync(
                envelope, interceptors, wireDispatcher, logger, cancellationToken);

            if (result.Error is not null)
            {
                return Results.Json(result, statusCode: MapStatusCode(result.Error.Code));
            }

            return Results.Ok(result);
        });

        endpoints.MapPost(path + "/publish", async (
            DispatchEnvelope envelope,
            IEnumerable<IDispatchInterceptor> interceptors,
            IWireDispatcher wireDispatcher,
            ILogger<IWireDispatcher> logger,
            CancellationToken cancellationToken) =>
        {
            await DispatchPipeline.ExecuteEventAsync(
                envelope, interceptors, wireDispatcher, logger, cancellationToken);

            return Results.Ok();
        });

        return endpoints;
    }

    private static int MapStatusCode(string code) => code switch
    {
        "NotFound" => 404,
        "Unauthorized" => 401,
        "Conflict" => 409,
        "InternalError" => 500,
        _ => 400,
    };
}
