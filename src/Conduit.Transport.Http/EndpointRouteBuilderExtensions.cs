using Conduit.Exceptions;
using Conduit.Runtime;
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
    /// <param name="basePath">The base URL path for Conduit endpoints. Defaults to "/api/conduit".
    /// Dispatch maps to "{basePath}/dispatch", publish maps to "{basePath}/publish".</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapConduitEndpoint(
        this IEndpointRouteBuilder endpoints,
        string basePath = "/api/conduit")
    {
        endpoints.MapPost(basePath + "/dispatch", async (
            DispatchEnvelope envelope,
            IWireMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.HandleWireRequestAsync(envelope, cancellationToken);

            if (result.Error is not null)
            {
                return Results.Json(result, statusCode: ErrorCode.ToHttpStatusCode(result.Error.Code));
            }

            return Results.Ok(result);
        });

        endpoints.MapPost(basePath + "/publish", async (
            DispatchEnvelope envelope,
            IWireMediator mediator,
            CancellationToken cancellationToken) =>
        {
            await mediator.HandleWireEventAsync(envelope, cancellationToken);
            return Results.Ok();
        });

        return endpoints;
    }
}
