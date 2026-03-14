using System.Text.Json;
using Conduit.Dispatch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Dual-mode SignalR transport for Conduit messaging.
/// In server mode (constructed with <see cref="IHubContext{ConduitHub}"/>), pushes events
/// to connected clients. In client mode (constructed with <see cref="HubConnection"/>),
/// sends commands to a remote hub and receives events.
/// </summary>
public class SignalRTransport : IConduitTransport, IAsyncDisposable
{
    private readonly IHubContext<ConduitHub>? hubContext;
    private readonly HubConnection? hubConnection;
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private bool connected;

    /// <summary>
    /// Creates a server-mode transport that pushes events to connected SignalR clients.
    /// </summary>
    /// <param name="hubContext">The hub context for sending to clients.</param>
    public SignalRTransport(IHubContext<ConduitHub> hubContext)
    {
        this.hubContext = hubContext;
    }

    /// <summary>
    /// Creates a client-mode transport that connects to a remote SignalR hub.
    /// </summary>
    /// <param name="hubConnection">The hub connection to the remote service.</param>
    public SignalRTransport(HubConnection hubConnection)
    {
        this.hubConnection = hubConnection;
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

        await this.EnsureConnectedAsync(cancellationToken);

        var result = await this.hubConnection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", envelope.Type, envelope.Payload, cancellationToken);

        if (result?.Error is not null)
        {
            throw new InvalidOperationException(
                $"Remote dispatch failed: [{result.Error.Code}] {result.Error.Message}");
        }

        return result?.Data;
    }

    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.hubContext is not null)
        {
            await this.hubContext.Clients.All.SendAsync(envelope.Type, envelope.Payload, cancellationToken);
            return;
        }

        if (this.hubConnection is not null)
        {
            await this.EnsureConnectedAsync(cancellationToken);
            await this.hubConnection.InvokeAsync("Publish", envelope.Type, envelope.Payload, cancellationToken);
            return;
        }

        throw new InvalidOperationException("Transport not configured.");
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (this.connected)
        {
            return;
        }

        await this.connectLock.WaitAsync(cancellationToken);
        try
        {
            if (this.connected)
            {
                return;
            }

            await this.hubConnection!.StartAsync(cancellationToken);
            this.connected = true;
        }
        finally
        {
            this.connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.hubConnection is not null)
        {
            await this.hubConnection.DisposeAsync();
        }

        this.connectLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
