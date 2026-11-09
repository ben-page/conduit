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
    /// Enriches and starts the dispatch context for an inbound HTTP request. On an edge that trusts its callers, the
    /// <c>X-Conduit-*</c> correlation headers (cross-service propagation) are honored when the message and conversation
    /// ids are both present and every supplied id is a plain token (<see cref="MessageContextIds.IsValid"/>). Anywhere
    /// else, and for any other set of headers, the caller's ids are ignored and the ids are the server's: fresh ones are
    /// minted (a self-causating head dispatch) so enriched properties still reach the mediator, and when no enricher
    /// contributed a property no context is started, so the mediator mints one.
    /// </summary>
    /// <param name="httpContext">The inbound request's context (carries <c>User</c> and headers).</param>
    /// <param name="enrichers">The registered server-trusted enrichers. May be empty.</param>
    /// <param name="trustCallerContext">Whether this edge honors the correlation ids its callers send: only the wire
    /// endpoint mapped with <c>trustCallerContext: true</c> does; REST endpoints and minimal-API routes never do.</param>
    public static void Start(
        HttpContext httpContext, IEnumerable<IDispatchContextEnricher> enrichers, bool trustCallerContext)
    {
        // HTTP transports carry no client-supplied properties; enricher writes are authoritative.
        var properties = DispatchContextEnrichment.Apply(
            enrichers, httpContext.User, clientProperties: null);

        if (trustCallerContext && ReadCallerIds(httpContext.Request.Headers) is var (messageId, causationId, conversationId))
        {
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId, properties);
            return;
        }

        // The ids are the server's: mint fresh ones so enriched properties still reach the mediator.
        if (properties is not null)
        {
            var mid = Guid.NewGuid().ToString("N");
            MessageContext.StartDispatchFromTransport(mid, causationId: null, conversationId: mid, properties);
        }
    }

    /// <summary>
    /// The caller's correlation ids, or null unless the message and conversation ids are both present and every
    /// supplied id (the causation id is optional) is a plain token: one bad id discards the set whole.
    /// </summary>
    private static (string MessageId, string? CausationId, string ConversationId)? ReadCallerIds(IHeaderDictionary headers)
    {
        var messageId = headers["X-Conduit-Message-Id"].ToString();
        var conversationId = headers["X-Conduit-Conversation-Id"].ToString();
        var causationId = headers["X-Conduit-Causation-Id"].ToString();

        if (!MessageContextIds.IsValid(messageId) || !MessageContextIds.IsValid(conversationId))
        {
            return null;
        }

        if (causationId.Length == 0)
        {
            return (messageId, null, conversationId);
        }

        return MessageContextIds.IsValid(causationId) ? (messageId, causationId, conversationId) : null;
    }
}
