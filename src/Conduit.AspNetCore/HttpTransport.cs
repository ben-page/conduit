using System.Net.Http.Json;
using System.Text.Json;
using Conduit.Messages;
using Conduit.Routing;

namespace Conduit.AspNetCore;

/// <summary>
/// Sends commands and queries to a remote Conduit endpoint via HTTP POST.
/// The remote endpoint is expected to expose a <c>/api/dispatch</c> route
/// mapped by <see cref="ConduitEndpoints.MapConduit"/>.
/// </summary>
public class HttpTransport(HttpClient httpClient, string path = "/api/dispatch") : IConduitTransport
{
    /// <inheritdoc />
    public Task DeliverAsync(IEvent @event, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "HTTP transport does not support fire-and-forget event delivery. " +
            "Use SignalR or a message bus transport for events.");
    }

    /// <inheritdoc />
    public async Task<object?> SendAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(path, envelope, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DispatchResult<JsonElement?>>(cancellationToken);

        if (result?.Error is not null)
        {
            throw new InvalidOperationException(
                $"Remote dispatch failed: [{result.Error.Code}] {result.Error.Message}");
        }

        return result?.Data;
    }
}
