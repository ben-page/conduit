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
    /// <summary>The header prefix a client property travels under: <c>X-Conduit-Property-TenantCode</c> carries
    /// <c>TenantCode</c>. Matched in any letter case, as header names are.</summary>
    internal const string PropertyHeaderPrefix = "X-Conduit-Property-";

    /// <summary>
    /// Enriches and starts the dispatch context for an inbound HTTP request. On an edge that trusts its callers, the
    /// <c>X-Conduit-*</c> correlation headers (cross-service propagation) are honored when the message and conversation
    /// ids are both present and every supplied id is a plain token (<see cref="MessageContextIds.IsValid"/>). Anywhere
    /// else, and for any other set of headers, the caller's ids are ignored and the ids are the server's: fresh ones are
    /// minted (a self-causating head dispatch) so the properties still reach the mediator, and when there are none (no
    /// client property read, no enricher contributed) no context is started, so the mediator mints one.
    /// </summary>
    /// <param name="httpContext">The inbound request's context (carries <c>User</c> and headers).</param>
    /// <param name="enrichers">The registered server-trusted enrichers. May be empty.</param>
    /// <param name="trustCallerContext">Whether this edge honors the correlation ids its callers send: only the wire
    /// endpoint mapped with <c>trustCallerContext: true</c> does; REST endpoints and minimal-API routes never do.</param>
    /// <param name="readClientProperties">Whether this edge seeds the properties from the request's
    /// <c>X-Conduit-Property-*</c> headers, as the hub seeds them from its <c>headers</c> argument: the wire endpoint's
    /// <c>/dispatch</c> and <c>/publish</c> do; REST endpoints and minimal-API routes never do. Enrichers run after, so
    /// their writes win over a client property of the same name.</param>
    public static void Start(
        HttpContext httpContext,
        IEnumerable<IDispatchContextEnricher> enrichers,
        bool trustCallerContext,
        bool readClientProperties)
    {
        var properties = DispatchContextEnrichment.Apply(
            enrichers,
            httpContext.User,
            readClientProperties ? ReadClientProperties(httpContext.Request.Headers) : null);

        if (trustCallerContext && ReadCallerIds(httpContext.Request.Headers) is var (messageId, causationId, conversationId))
        {
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId, properties);
            return;
        }

        // The ids are the server's: mint fresh ones so the properties still reach the mediator.
        if (properties is not null)
        {
            var mid = Guid.NewGuid().ToString("N");
            MessageContext.StartDispatchFromTransport(mid, causationId: null, conversationId: mid, properties);
        }
    }

    /// <summary>
    /// The client's properties: each <c>X-Conduit-Property-&lt;name&gt;</c> header (prefix in any letter case) as
    /// <c>name</c>, spelled as it arrived, with its value as a string; null when the request carries none. HTTP/2 and
    /// HTTP/3 deliver every header name in lower case, so over them a name arrives lower-cased.
    /// </summary>
    private static Dictionary<string, object?>? ReadClientProperties(IHeaderDictionary headers)
    {
        Dictionary<string, object?>? properties = null;
        foreach (var (name, value) in headers)
        {
            if (name.Length > PropertyHeaderPrefix.Length
                && name.StartsWith(PropertyHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                properties ??= new Dictionary<string, object?>();
                properties[name[PropertyHeaderPrefix.Length..]] = value.ToString();
            }
        }

        return properties;
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
