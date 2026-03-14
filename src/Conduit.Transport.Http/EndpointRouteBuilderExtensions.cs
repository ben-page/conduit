using Conduit.Dispatch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Conduit.Transport.Http;

/// <summary>
/// Extension methods for mapping HTTP dispatch endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps POST endpoints that accept <see cref="DispatchEnvelope"/> payloads
    /// and dispatch them through the <see cref="IWireMediator"/>.
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
            IWireMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.HandleWireRequestAsync(envelope, cancellationToken);

            if (result.Error is not null)
            {
                return Results.Json(result, statusCode: MapStatusCode(result.Error.Code));
            }

            return Results.Ok(result);
        });

        endpoints.MapPost(path + "/publish", async (
            DispatchEnvelope envelope,
            IWireMediator mediator,
            CancellationToken cancellationToken) =>
        {
            await mediator.HandleWireEventAsync(envelope, cancellationToken);
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
