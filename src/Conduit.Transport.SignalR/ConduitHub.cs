using System.Text.Json;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;

namespace Conduit.Transport.SignalR;

/// <summary>
/// SignalR hub that accepts dispatch requests from connected clients
/// and delegates to <see cref="IWireMediator"/> for processing.
/// Browser clients omit the context parameter (null) — the server mints a fresh
/// <see cref="MessageContext"/>. C# clients pass context for cross-service propagation.
/// Registered <see cref="IDispatchContextEnricher"/> instances contribute server-trusted
/// properties (e.g. the authenticated user id) over the client-supplied headers.
/// </summary>
public class ConduitHub(
    IWireMediator mediator,
    IEnumerable<IDispatchContextEnricher> enrichers) : Hub
{
    /// <summary>
    /// Dispatches a command or query received from a SignalR client.
    /// </summary>
    /// <param name="type">The fully-qualified message type name.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="context">Optional message context for cross-service propagation.
    /// Keys: MessageId, CausationId, ConversationId. Null when sent from browser clients.</param>
    /// <param name="headers">Optional caller-supplied headers (e.g. tenant code) made available
    /// to pipeline behaviors as entries in <see cref="MessageContext.Properties"/>.</param>
    public async Task<DispatchResult<object>> Dispatch(
        string type, JsonElement payload,
        Dictionary<string, string>? context = null,
        Dictionary<string, string>? headers = null)
    {
        this.StartMessageContext(context, headers);

        return await mediator.HandleWireRequestAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Publishes an event received from a SignalR client.
    /// </summary>
    /// <param name="type">The fully-qualified event type name.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="context">Optional message context for cross-service propagation.
    /// Keys: MessageId, CausationId, ConversationId. Null when sent from browser clients.</param>
    /// <param name="headers">Optional caller-supplied headers (e.g. tenant code) made available
    /// to pipeline behaviors as entries in <see cref="MessageContext.Properties"/>.</param>
    public async Task Publish(
        string type, JsonElement payload,
        Dictionary<string, string>? context = null,
        Dictionary<string, string>? headers = null)
    {
        this.StartMessageContext(context, headers);

        await mediator.HandleWireEventAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
    }

    private void StartMessageContext(
        Dictionary<string, string>? context,
        Dictionary<string, string>? headers)
    {
        // Seed from client headers, then let server-side enrichers overwrite spoofable values.
        var properties = DispatchContextEnrichment.Apply(
            enrichers, this.Context.User, ToProperties(headers));

        if (context is not null
            && context.TryGetValue("MessageId", out var messageId)
            && context.TryGetValue("ConversationId", out var conversationId)
            && !string.IsNullOrWhiteSpace(messageId)
            && !string.IsNullOrWhiteSpace(conversationId))
        {
            context.TryGetValue("CausationId", out var causationId);
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId, properties);
            return;
        }

        // Properties present without correlation: mint fresh IDs so they reach the generated mediator.
        if (properties is not null)
        {
            var mid = Guid.NewGuid().ToString("N");
            MessageContext.StartDispatchFromTransport(mid, causationId: null, conversationId: mid, properties);
        }
    }

    private static Dictionary<string, object?>? ToProperties(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var props = new Dictionary<string, object?>(headers.Count);
        foreach (var (key, value) in headers)
        {
            props[key] = value;
        }

        return props;
    }
}
