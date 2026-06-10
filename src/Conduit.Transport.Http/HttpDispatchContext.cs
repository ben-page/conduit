using Conduit.Runtime;
using Microsoft.AspNetCore.Http;

namespace Conduit.Transport.Http;

/// <summary>
/// Shared helper that establishes the inbound <see cref="MessageContext"/> for HTTP-based transports —
/// both the generic wire endpoint (<see cref="EndpointRouteBuilderExtensions.MapConduitEndpoint"/>) and
/// the per-command REST endpoints (<see cref="ConduitRestHandlers"/>). It runs the registered
/// <see cref="IDispatchContextEnricher"/> instances over the authenticated principal so server-trusted
/// values (e.g. the user id) are carried on <see cref="MessageContext.Properties"/> uniformly, no matter
/// which HTTP entry point received the request. This is what keeps audit/current-user attribution
/// transport-independent across the hub, the wire endpoint, and REST-mapped commands.
/// </summary>
internal static class HttpDispatchContext
{
    /// <summary>
    /// Enriches and starts the dispatch context for an inbound HTTP request. Honors inbound
    /// <c>X-Conduit-*</c> correlation headers (cross-service propagation) when present, otherwise mints
    /// fresh ids so enriched properties still reach the mediator. When nothing is contributed (no
    /// enrichers and no correlation headers) it starts no transport context, preserving the prior
    /// behavior where the mediator mints a fresh local context.
    /// </summary>
    /// <param name="httpContext">The inbound request's context (carries <c>User</c> and headers).</param>
    /// <param name="enrichers">The registered server-trusted enrichers. May be empty.</param>
    public static void Start(HttpContext httpContext, IEnumerable<IDispatchContextEnricher> enrichers)
    {
        // HTTP transports carry no client-supplied properties; enricher writes are authoritative.
        var properties = DispatchContextEnrichment.Apply(
            enrichers, httpContext.User, clientProperties: null);

        var headers = httpContext.Request.Headers;
        var messageId = headers["X-Conduit-Message-Id"].ToString();
        var conversationId = headers["X-Conduit-Conversation-Id"].ToString();

        if (!string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(conversationId))
        {
            var causationId = headers["X-Conduit-Causation-Id"].ToString();
            MessageContext.StartDispatchFromTransport(
                messageId,
                string.IsNullOrWhiteSpace(causationId) ? null : causationId,
                conversationId,
                properties);
            return;
        }

        // No correlation headers: mint fresh ids so enriched properties still reach the mediator.
        if (properties is not null)
        {
            var mid = Guid.NewGuid().ToString("N");
            MessageContext.StartDispatchFromTransport(mid, causationId: null, conversationId: mid, properties);
        }
    }
}
