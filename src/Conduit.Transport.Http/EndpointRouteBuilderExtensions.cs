using System.Text.Json;
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
    /// Maps POST endpoints for Conduit wire dispatch. The message type is read from the
    /// <c>X-Conduit-Type</c> header and the request body contains the plain serialized message.
    /// A refused event (the type filter, an unknown type) is answered on <c>/publish</c> as <c>/dispatch</c> answers a
    /// refused request: the <see cref="DispatchResult{T}"/>, status-coded from its error.
    /// </summary>
    /// <remarks>
    /// The dispatch's correlation ids are the server's unless <paramref name="trustCallerContext"/> says otherwise:
    /// by default the <c>X-Conduit-Message-Id</c>, <c>X-Conduit-Conversation-Id</c> and <c>X-Conduit-Causation-Id</c>
    /// headers are ignored and the server mints fresh ids, because the inbox deduplicates on the MessageId and audit
    /// lineage follows the other two. A trusting edge honors them only when the message and conversation ids are both
    /// present and every supplied id is a plain token (<see cref="MessageContextIds.IsValid"/>); otherwise it mints
    /// too. Either way the request is answered as usual. Server-trusted properties from the registered
    /// <see cref="IDispatchContextEnricher"/>s apply in every case.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="basePath">The base URL path for Conduit endpoints. Defaults to "/api/conduit".
    /// Dispatch maps to "{basePath}/dispatch", publish maps to "{basePath}/publish".</param>
    /// <param name="trustCallerContext">Whether this edge honors the correlation ids its callers send: true only for
    /// an endpoint that service-to-service callers (<see cref="HttpTransport"/>) reach and browsers do not.</param>
    /// <returns>The route group both endpoints are mapped in, so a convention chained on it (e.g.
    /// <c>RequireAuthorization()</c>) applies to both.</returns>
    public static RouteGroupBuilder MapConduitEndpoint(
        this IEndpointRouteBuilder endpoints,
        string basePath = "/api/conduit",
        bool trustCallerContext = false)
    {
        var group = endpoints.MapGroup(basePath);

        group.MapPost("/dispatch", async (
            HttpRequest httpRequest,
            IWireMediator mediator,
            IEnumerable<IDispatchContextEnricher> enrichers,
            CancellationToken cancellationToken) =>
        {
            var envelope = await ReadEnvelopeAsync(httpRequest, cancellationToken);
            HttpDispatchContext.Start(httpRequest.HttpContext, enrichers, trustCallerContext, readClientProperties: true);

            // The edge where the answer leaves the server for a person: the refusal is written in the reader's language here.
            return Answer(RefusalRendering.RenderResult(
                await mediator.HandleWireRequestAsync(envelope, cancellationToken),
                httpRequest.HttpContext.RequestServices,
                MessageContext.Current));
        });

        group.MapPost("/publish", async (
            HttpRequest httpRequest,
            IWireMediator mediator,
            IEnumerable<IDispatchContextEnricher> enrichers,
            CancellationToken cancellationToken) =>
        {
            var envelope = await ReadEnvelopeAsync(httpRequest, cancellationToken);
            HttpDispatchContext.Start(httpRequest.HttpContext, enrichers, trustCallerContext, readClientProperties: true);

            try
            {
                await mediator.HandleWireEventAsync(envelope, cancellationToken);
            }
            catch (ConduitException refusal)
            {
                // A refused event (the type filter, an unknown type) is answered as /dispatch answers a refused request.
                return Answer(RefusalRendering.RenderResult(
                    DispatchResult<object>.Fail(refusal),
                    httpRequest.HttpContext.RequestServices,
                    MessageContext.Current));
            }

            return Results.Ok();
        });

        return group;
    }

    /// <summary>
    /// The HTTP answer for a dispatch result: 200 with the result, or the whole result status-coded from
    /// <see cref="DispatchError.Status"/> (from the code when a server predating it sent none).
    /// </summary>
    private static IResult Answer(DispatchResult<object> result)
    {
        if (result.Error is not null)
        {
            return Results.Json(result, ConduitRuntime.JsonOptions, statusCode: result.Error.Status > 0
                ? result.Error.Status
                : ErrorCode.ToHttpStatusCode(result.Error.Code));
        }

        return Results.Json(result, ConduitRuntime.JsonOptions);
    }

    private static async Task<DispatchEnvelope> ReadEnvelopeAsync(
        HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        var type = httpRequest.Headers["X-Conduit-Type"].ToString();
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new BadHttpRequestException("Missing required X-Conduit-Type header.");
        }

        var payload = await JsonSerializer.DeserializeAsync<JsonElement>(
            httpRequest.Body, ConduitRuntime.JsonOptions, cancellationToken);

        return new DispatchEnvelope(type, payload);
    }
}
