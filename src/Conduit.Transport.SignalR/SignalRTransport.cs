using System.Text.Json;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Dual-mode SignalR transport for Conduit messaging.
/// In server mode (constructed with <see cref="IHubContext{ConduitHub}"/>), pushes events
/// to connected clients. In client mode (constructed with <see cref="HubConnection"/>),
/// sends commands, queries and events to a remote hub; it does not receive the hub's events.
/// </summary>
public class SignalRTransport : IConduitTransport, IAsyncDisposable
{
    private readonly IHubContext<ConduitHub>? hubContext;
    private readonly HubConnection? hubConnection;
    private readonly ConduitTypeRegistry? typeRegistry;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private volatile bool disposed;

    /// <summary>
    /// Creates a server-mode transport that pushes events to connected SignalR clients.
    /// </summary>
    /// <param name="hubContext">The hub context for sending to clients.</param>
    public SignalRTransport(IHubContext<ConduitHub> hubContext)
    {
        this.hubContext = hubContext;
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
    /// Server mode: pushes the event to every connected client. Client mode: sends a message that expects no reply to
    /// the remote hub — an event to its <c>Publish</c>, anything else (a routed <c>[FireAndForget]</c> command, the only
    /// one that reaches here) to its <c>Dispatch</c>, the result discarded but a refusal still thrown.
    /// </summary>
    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.hubContext is not null)
        {
            await this.hubContext.Clients.All.SendAsync(envelope.Type, envelope.Payload, cancellationToken);
            return;
        }

        await this.SendWithoutReplyAsync(this.hubConnection!, envelope, cancellationToken);
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
