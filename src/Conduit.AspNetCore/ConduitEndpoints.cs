using Conduit.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Conduit.AspNetCore;

public static class ConduitEndpoints
{
    public static IEndpointRouteBuilder MapConduit(
        this IEndpointRouteBuilder endpoints,
        string path = "/api/dispatch")
    {
        endpoints.MapPost(path, async (
            DispatchEnvelope envelope,
            IWireDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await dispatcher.DispatchAsync(
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
            catch (Exception)
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
