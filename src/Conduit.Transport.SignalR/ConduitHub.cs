using System.Text.Json;
using Conduit.Exceptions;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Transport.SignalR;

/// <summary>
/// SignalR hub that accepts dispatch requests from connected clients
/// and delegates to <see cref="IWireMediator"/> for processing.
/// The server mints every dispatch's <see cref="MessageContext"/> ids and ignores a caller's context argument, unless
/// the hub was registered with <see cref="ConduitSignalROptions.TrustCallerContext"/> (a service-to-service edge, whose
/// C# clients pass their context for cross-service propagation); even then a context whose ids are not all plain
/// tokens (<see cref="MessageContextIds.IsValid"/>) is ignored. Browser clients pass null.
/// Registered <see cref="IDispatchContextEnricher"/> instances contribute server-trusted
/// properties (e.g. the authenticated user id) over the client-supplied headers.
/// A host that registers a <see cref="ConduitServerInfo"/> has it sent to each client as it connects.
/// A failed dispatch's message and details are written in the reader's language on the way out
/// (<see cref="RefusalRendering.RenderResult{T}"/>, from the hub invocation's service scope).
/// An unexpected failure (<c>InternalError</c>, a listener's failure, a failure outside the listeners) is reported to
/// each registered <see cref="IInboundFailureObserver"/> before it is answered (the inbox releases its record, so a retry
/// under the same MessageId runs); a refusal is not.
/// </summary>
public class ConduitHub(
    IWireMediator mediator,
    IEnumerable<IDispatchContextEnricher> enrichers,
    IEnumerable<ConduitServerInfo> serverInfo,
    IOptions<ConduitSignalROptions> options,
    IServiceProvider services,
    ILogger<ConduitHub> logger) : Hub
{
    private static readonly Action<ILogger, string, string, Exception?> LogFailureObserverFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(101, "FailureObserverFailed"),
            "Inbound failure observer '{Observer}' threw for a failed message of type '{MessageType}'; the answer and "
                + "the other observers are unaffected");

    /// <summary>The client method <see cref="OnConnectedAsync"/> invokes with the <see cref="ConduitServerInfo"/>.</summary>
    public const string ServerInfoMethod = "ConduitServerInfo";

    /// <summary>
    /// Sends the caller this server's <see cref="ConduitServerInfo"/> when the host registered one, and nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Sent on connect, not broadcast on restart: a restart broadcast reaches the clients connected to that pod, which at
    /// startup is nobody, while the clients that need it are still on the old pod and events do not replay. Every client
    /// that needs it necessarily connects, and this runs again on every reconnect.
    /// </remarks>
    /// <returns>A task that completes once the message is sent.</returns>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        var info = serverInfo.LastOrDefault();
        if (info is not null)
        {
            await this.Clients.Caller.SendAsync(ServerInfoMethod, info, this.Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Dispatches a command or query received from a SignalR client. A refusal that carries a message name is written
    /// in the reader's language here, where the answer leaves the server.
    /// </summary>
    /// <param name="type">The fully-qualified message type name.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="context">Optional message context for cross-service propagation, honored only by a hub registered
    /// with <see cref="ConduitSignalROptions.TrustCallerContext"/>. Keys: MessageId, CausationId, ConversationId.
    /// Null when sent from browser clients.</param>
    /// <param name="headers">Optional caller-supplied headers (e.g. tenant code) made available
    /// to pipeline behaviors as entries in <see cref="MessageContext.Properties"/>.</param>
    public async Task<DispatchResult<object>> Dispatch(
        string type, JsonElement payload,
        Dictionary<string, string>? context = null,
        Dictionary<string, string>? headers = null)
    {
        this.StartMessageContext(context, headers);

        var result = await mediator.HandleWireRequestAsync(
            new DispatchEnvelope(type, payload),
            this.Context.ConnectionAborted);
        if (result.Error is { } error && error.Code == ErrorCode.InternalError.Value)
        {
            await this.ReportFailureAsync(type, error);
        }

        return RefusalRendering.RenderResult(result, services, MessageContext.Current);
    }

    /// <summary>
    /// Publishes an event received from a SignalR client. A refusal (the type filter, an unknown type) fails the
    /// invocation with a <see cref="HubException"/> carrying the refusal's message, written in the reader's language. A
    /// listener's failure (<see cref="EventListenerException"/>), and a dispatch that failed outside the listeners
    /// (<see cref="EventDispatchException"/>: a pipeline behavior, the listeners' resolution, a gate that threw), fail it
    /// with the fixed message of an unexpected failure; the exception stays in the log, under a support reference.
    /// </summary>
    /// <param name="type">The fully-qualified event type name.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="context">Optional message context for cross-service propagation, honored only by a hub registered
    /// with <see cref="ConduitSignalROptions.TrustCallerContext"/>. Keys: MessageId, CausationId, ConversationId.
    /// Null when sent from browser clients.</param>
    /// <param name="headers">Optional caller-supplied headers (e.g. tenant code) made available
    /// to pipeline behaviors as entries in <see cref="MessageContext.Properties"/>.</param>
    public async Task Publish(
        string type, JsonElement payload,
        Dictionary<string, string>? context = null,
        Dictionary<string, string>? headers = null)
    {
        this.StartMessageContext(context, headers);

        try
        {
            await mediator.HandleWireEventAsync(
                new DispatchEnvelope(type, payload),
                this.Context.ConnectionAborted);
        }
        catch (ConduitException refusal)
        {
            // Publish answers nothing, so the refusal fails the invocation: a HubException's message reaches the client.
            var error = RefusalRendering.RenderResult(DispatchResult<object>.Fail(refusal), services, MessageContext.Current).Error!;
            throw new HubException(error.Message);
        }
        catch (EventListenerException failure)
        {
            // A listener failed (every listener ran, each failure logged): the first failure is logged here under a
            // support reference and the invocation fails with the fixed message, as below. Left to SignalR it was a
            // generic invocation error, and with EnableDetailedErrors the listener's message.
            var unexpected = ConduitRuntime.UnexpectedEventFailure(logger, type, failure);
            await this.ReportFailureAsync(type, unexpected.Error!);
            var error = RefusalRendering.RenderResult(unexpected, services, MessageContext.Current).Error!;
            throw new HubException(error.Message);
        }
        catch (EventDispatchException failure)
        {
            // The dispatch failed outside the listeners (a behavior, the listeners' resolution, a gate that threw), logged
            // with its stack: the invocation fails with the fixed message an unexpected failure is answered with, never
            // the exception (a HubException carries a message only, so the reference stays in the log line).
            var unexpected = ConduitRuntime.UnexpectedFailure(failure.Reference);
            await this.ReportFailureAsync(type, unexpected.Error!);
            var error = RefusalRendering.RenderResult(unexpected, services, MessageContext.Current).Error!;
            throw new HubException(error.Message);
        }
    }

    /// <summary>
    /// Reports an unexpected failure to every <see cref="IInboundFailureObserver"/> in the invocation's services, each in
    /// its own guard: one that throws (or observers that cannot be resolved) is logged at Warning, EventId 101
    /// <c>FailureObserverFailed</c>, and never changes the answer. The observers get <see cref="CancellationToken.None"/>:
    /// a caller that has gone still retries, which is what the inbox's release is for.
    /// </summary>
    private async Task ReportFailureAsync(string messageType, DispatchError error)
    {
        IInboundFailureObserver[] observers;
        try
        {
            observers = [.. services.GetServices<IInboundFailureObserver>()];
        }
#pragma warning disable CA1031 // Observers that cannot be built are logged; the caller still gets its answer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailureObserverFailed(logger, nameof(IInboundFailureObserver), messageType, ex);
            return;
        }

        foreach (var observer in observers)
        {
            try
            {
                await observer.OnDispatchFailedAsync(messageType, error, CancellationToken.None);
            }
#pragma warning disable CA1031 // An observer that throws is logged; the caller still gets its answer, the others still run.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailureObserverFailed(logger, observer.GetType().Name, messageType, ex);
            }
        }
    }

    private void StartMessageContext(
        Dictionary<string, string>? context,
        Dictionary<string, string>? headers)
    {
        // Seed from client headers, then let server-side enrichers overwrite spoofable values.
        var properties = DispatchContextEnrichment.Apply(
            enrichers, this.Context.User, ToProperties(headers));

        if (options.Value.TrustCallerContext && ReadCallerIds(context) is var (messageId, causationId, conversationId))
        {
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId, properties);
            return;
        }

        // The ids are the server's: mint fresh ones so properties still reach the generated mediator.
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
    private static (string MessageId, string? CausationId, string ConversationId)? ReadCallerIds(
        Dictionary<string, string>? context)
    {
        if (context is null
            || !context.TryGetValue("MessageId", out var messageId)
            || !context.TryGetValue("ConversationId", out var conversationId)
            || !MessageContextIds.IsValid(messageId)
            || !MessageContextIds.IsValid(conversationId))
        {
            return null;
        }

        // An empty causation id is an absent one, as on the HTTP edge (a missing header reads as empty).
        if (!context.TryGetValue("CausationId", out var causationId) || string.IsNullOrEmpty(causationId))
        {
            return (messageId, null, conversationId);
        }

        return MessageContextIds.IsValid(causationId) ? (messageId, causationId, conversationId) : null;
    }

    private static Dictionary<string, object?>? ToProperties(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        // Property names are case-insensitive at every edge, as over HTTP (where HTTP/2 lower-cases header names).
        var props = new Dictionary<string, object?>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            props[key] = value;
        }

        return props;
    }
}
