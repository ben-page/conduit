using System.Collections.Frozen;
using System.Text.Json;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Dual-mode SignalR transport for Conduit messaging.
/// In server mode (constructed with <see cref="IHubContext{ConduitHub}"/>), pushes events
/// to connected clients: a <c>[ClientVisible]</c> event to every connection and any other to none, unless an
/// <see cref="IEventRecipientResolver"/> decides. In client mode (constructed with <see cref="HubConnection"/>),
/// sends commands, queries and events to a remote hub; it does not receive the hub's events.
/// </summary>
public class SignalRTransport : IConduitTransport, IAsyncDisposable
{
    // The SignalR package's first EventId, in the 100s each transport package numbers its own from.
    private static readonly Action<ILogger, string, Exception?> LogRecipientResolutionFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(100, "RecipientResolutionFailed"),
            "The event recipient resolver failed for '{EventType}'; the event is not pushed to any SignalR client");

    private readonly ILogger logger = NullLogger.Instance;
    private readonly IHubContext<ConduitHub>? hubContext;
    private readonly IEventRecipientResolver? recipientResolver;
    private readonly FrozenDictionary<string, Type>? eventTypesByName;
    private readonly HubConnection? hubConnection;
    private readonly ConduitTypeRegistry? typeRegistry;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private volatile bool disposed;

    /// <summary>
    /// Creates a server-mode transport that pushes events to connected SignalR clients.
    /// </summary>
    /// <param name="hubContext">The hub context for sending to clients.</param>
    /// <param name="typeRegistry">
    /// This host's message types. Without <paramref name="recipientResolver"/>, an event its
    /// <see cref="ConduitTypeRegistry.ClientVisibleEventTypeNames"/> lists goes to every connection and any other to
    /// none; with one, the resolver is handed the event deserialized as the <see cref="ConduitTypeRegistry.EventTypes"/>
    /// entry of that name.
    /// </param>
    /// <param name="logger">
    /// Logs a resolver that throws (or a payload that does not read as its event type): that event is pushed to nobody,
    /// and the send does not throw.
    /// </param>
    /// <param name="recipientResolver">
    /// Decides who receives every event, in place of the <c>[ClientVisible]</c> default. <c>AddConduitSignalR</c> passes
    /// the one registered in DI, if any.
    /// </param>
    public SignalRTransport(
        IHubContext<ConduitHub> hubContext,
        ConduitTypeRegistry typeRegistry,
        ILogger<SignalRTransport> logger,
        IEventRecipientResolver? recipientResolver = null)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(typeRegistry);
        ArgumentNullException.ThrowIfNull(logger);

        this.logger = logger;
        this.hubContext = hubContext;
        this.typeRegistry = typeRegistry;
        this.recipientResolver = recipientResolver;
        if (recipientResolver is not null)
        {
            // The generator lists each name once (CONDUIT008 refuses a name two assemblies declare); a hand-built registry
            // that repeats one keeps the first rather than failing to construct the transport.
            this.eventTypesByName = typeRegistry.EventTypes
                .DistinctBy(type => type.FullName, StringComparer.Ordinal)
                .ToFrozenDictionary(type => type.FullName!, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Creates a client-mode transport that connects to a remote SignalR hub. The connection starts on the first send,
    /// and starts again on a later send once it is closed for good (automatic reconnect gave up).
    /// </summary>
    /// <param name="hubConnection">The hub connection to the remote service. The transport owns it and disposes it.</param>
    /// <param name="typeRegistry">
    /// This host's message types, which tell an event (the hub's <c>Publish</c>) from a command (its <c>Dispatch</c>)
    /// in <see cref="SendEventAsync"/> and <see cref="RedeliverAsync"/>. <c>UseSignalR</c> passes the host's; without
    /// one, every such send is a command.
    /// </param>
    public SignalRTransport(HubConnection hubConnection, ConduitTypeRegistry? typeRegistry = null)
    {
        this.hubConnection = hubConnection;
        this.typeRegistry = typeRegistry;
    }

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.hubConnection is null)
        {
            throw new NotSupportedException(
                "Server-mode transport cannot send outbound requests. " +
                "Use client-mode transport with a HubConnection for request/response.");
        }

        return await this.DispatchAsync(this.hubConnection, envelope, cancellationToken);
    }

    /// <summary>
    /// Server mode: pushes the event to the connections its recipients name — the registered
    /// <see cref="IEventRecipientResolver"/>'s answer, or without one every connection for a <c>[ClientVisible]</c> event
    /// and none for any other. Client mode: sends a message that expects no reply to the remote hub — an event to its
    /// <c>Publish</c>, anything else (a routed <c>[FireAndForget]</c> command, the only one that reaches here) to its
    /// <c>Dispatch</c>, the result discarded but a refusal still thrown.
    /// </summary>
    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.hubContext is not null)
        {
            await this.PushAsync(this.hubContext, envelope, cancellationToken);
            return;
        }

        await this.SendWithoutReplyAsync(this.hubConnection!, envelope, cancellationToken);
    }

    private async Task PushAsync(
        IHubContext<ConduitHub> context, DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        var recipients = await this.ResolveRecipientsAsync(envelope, cancellationToken);
        var names = recipients.Names;
        var clients = recipients.Kind switch
        {
            EventRecipients.RecipientKind.All => context.Clients.All,
            EventRecipients.RecipientKind.Users => names.Count > 0 ? context.Clients.Users(names) : null,
            EventRecipients.RecipientKind.Groups => names.Count > 0 ? context.Clients.Groups(names) : null,
            EventRecipients.RecipientKind.None => null,
            _ => null,
        };

        if (clients is not null)
        {
            await clients.SendAsync(envelope.Type, envelope.Payload, cancellationToken);
        }
    }

    private async ValueTask<EventRecipients> ResolveRecipientsAsync(
        DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        if (this.recipientResolver is null)
        {
            var clientVisible = this.typeRegistry!.ClientVisibleEventTypeNames.Contains(envelope.Type);
            return clientVisible ? EventRecipients.All : EventRecipients.None;
        }

        // A fault here skips this event's push and never throws: the outbox delivers through TransportDispatcher, which
        // does not isolate one transport from the next, so a throw would stop the transports after this one, fail the
        // outbox entry and resend it to those that already took it. A cancelled publish still propagates.
#pragma warning disable CA1031 // A resolver's fault, or an unreadable payload, must not fail the broadcast.
        try
        {
            // The resolver decides from the event itself, typed as it was published. A name the registry does not list
            // (no generated publish sends one; a hand-built envelope can) leaves it the raw payload.
            var @event = this.eventTypesByName!.TryGetValue(envelope.Type, out var eventType)
                ? envelope.Payload.Deserialize(eventType, ConduitRuntime.JsonOptions)
                    ?? throw new JsonException($"The payload of '{envelope.Type}' is null.")
                : envelope.Payload;
            return await this.recipientResolver.ResolveAsync(envelope.Type, @event, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            LogRecipientResolutionFailed(this.logger, envelope.Type, exception);
            return EventRecipients.None;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Client mode: replays a message to the remote hub as <see cref="SendEventAsync"/> sends it, the result discarded but
    /// a refusal still thrown, so a failed replay is not reported as delivered.
    /// </summary>
    /// <inheritdoc />
    public async Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.hubConnection is null)
        {
            throw new NotSupportedException(
                "Server-mode SignalR transport does not support RedeliverAsync. " +
                "Dead letter replay requires a client-mode transport with a HubConnection.");
        }

        await this.SendWithoutReplyAsync(this.hubConnection, envelope, cancellationToken);
    }

    private async Task SendWithoutReplyAsync(
        HubConnection connection, DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        if (this.typeRegistry is not null && this.typeRegistry.EventTypeNames.Contains(envelope.Type))
        {
            await this.EnsureConnectedAsync(connection, cancellationToken);
            Dictionary<string, string>? noHeaders = null;
            await connection.InvokeAsync(
                "Publish", envelope.Type, envelope.Payload, BuildContext(), noHeaders, cancellationToken);
            return;
        }

        _ = await this.DispatchAsync(connection, envelope, cancellationToken);
    }

    private async Task<object?> DispatchAsync(
        HubConnection connection, DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        await this.EnsureConnectedAsync(connection, cancellationToken);

        // The hub's Dispatch signature is (type, payload, context?, headers?).
        // SignalR rejects arity mismatches on the wire, so we always send
        // four args even when we have no headers to forward.
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", envelope.Type, envelope.Payload, BuildContext(), noHeaders, cancellationToken);

        return ConduitRuntime.UnwrapReply(result);
    }

    private static Dictionary<string, string>? BuildContext()
    {
        // Forwarded only when all three ids are plain tokens, as a trusting hub would honor them: a broker can hand this
        // host any id (a foreign producer's, CR/LF, kilobytes of it). Otherwise none is sent and the receiving hub mints.
        var context = MessageContext.Current;
        if (context is null
            || !MessageContextIds.IsValid(context.MessageId)
            || !MessageContextIds.IsValid(context.ConversationId)
            || !MessageContextIds.IsValid(context.CausationId))
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MessageId"] = context.MessageId,
            ["ConversationId"] = context.ConversationId,
            ["CausationId"] = context.CausationId,
        };

        return dict;
    }

    // Starts the connection whenever it is Disconnected: on first use, and again once it closed for good (automatic
    // reconnect gave up, or the hub closed it). Connecting/Reconnecting are left alone, so a send during an automatic
    // reconnect fails as SignalR fails it rather than racing the reconnect with a second start.
    private async Task EnsureConnectedAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (connection.State == HubConnectionState.Connected)
        {
            return;
        }

        await this.connectLock.WaitAsync(cancellationToken);
        try
        {
            if (connection.State == HubConnectionState.Disconnected)
            {
                await connection.StartAsync(cancellationToken);
            }
        }
        finally
        {
            this.connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Idempotent: the container disposes this instance once per registration that resolved it (the keyed
        // SignalRTransport and the keyed IConduitTransport forwarding to it).
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.hubConnection is not null)
        {
            await this.hubConnection.DisposeAsync();
        }

        this.connectLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
