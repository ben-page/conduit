using System.Net.Http.Json;
using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Transport.Http;

/// <summary>
/// Sends commands and queries to a remote Conduit endpoint via HTTP POST.
/// Used for C#-to-C# service communication.
/// </summary>
public class HttpTransport(HttpClient httpClient, string path = "/api/dispatch") : IConduitTransport
{
    /// <inheritdoc />
    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "HTTP transport does not support fire-and-forget event delivery. " +
            "Use SignalR or a message bus transport for events.");
    }

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var response = await httpClient.PostAsJsonAsync(path, envelope, cancellationToken);
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
