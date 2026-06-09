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
            HttpRequest httpRequest,
            IWireMediator mediator,
            IEnumerable<IDispatchContextEnricher> enrichers,
            CancellationToken cancellationToken) =>
        {
            var envelope = await ReadEnvelopeAsync(httpRequest, cancellationToken);
            StartMessageContext(httpRequest, enrichers);

            var result = await mediator.HandleWireRequestAsync(envelope, cancellationToken);

            if (result.Error is not null)
            {
                return Results.Json(result, statusCode: ErrorCode.ToHttpStatusCode(result.Error.Code));
            }

            return Results.Ok(result);
        });

        endpoints.MapPost(basePath + "/publish", async (
            HttpRequest httpRequest,
            IWireMediator mediator,
            IEnumerable<IDispatchContextEnricher> enrichers,
            CancellationToken cancellationToken) =>
        {
            var envelope = await ReadEnvelopeAsync(httpRequest, cancellationToken);
            StartMessageContext(httpRequest, enrichers);

            await mediator.HandleWireEventAsync(envelope, cancellationToken);
            return Results.Ok();
        });

        return endpoints;
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

    private static void StartMessageContext(
        HttpRequest httpRequest, IEnumerable<IDispatchContextEnricher> enrichers)
    {
        // The HTTP wire transport carries no client-supplied properties; enrichers contribute
        // server-trusted values from the authenticated principal. Their writes are authoritative.
        var properties = DispatchContextEnrichment.Apply(
            enrichers, httpRequest.HttpContext.User, clientProperties: null);

        var messageId = httpRequest.Headers["X-Conduit-Message-Id"].ToString();
        var conversationId = httpRequest.Headers["X-Conduit-Conversation-Id"].ToString();

        if (!string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(conversationId))
        {
            var causationId = httpRequest.Headers["X-Conduit-Causation-Id"].ToString();
            MessageContext.StartDispatchFromTransport(
                messageId,
                string.IsNullOrWhiteSpace(causationId) ? null : causationId,
                conversationId,
                properties);
            return;
        }

        // No correlation headers: mint fresh IDs so enriched properties still reach the mediator.
        if (properties is not null)
        {
            var mid = Guid.NewGuid().ToString("N");
            MessageContext.StartDispatchFromTransport(mid, causationId: null, conversationId: mid, properties);
        }
    }
}
