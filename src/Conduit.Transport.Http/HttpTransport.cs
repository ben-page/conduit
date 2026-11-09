using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Transport.Http;

/// <summary>
/// Sends commands and queries to a remote Conduit endpoint via HTTP POST.
/// Used for C#-to-C# service communication. The message type is sent in the
/// <c>X-Conduit-Type</c> header and the body contains the plain serialized message.
/// </summary>
public class HttpTransport(HttpClient httpClient, string basePath = "/api/conduit") : IConduitTransport
{
    /// <inheritdoc />
    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "HTTP transport does not support fire-and-forget event delivery. " +
            "Use SignalR or a message bus transport for events.");
    }

    /// <inheritdoc />
    public async Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // POST to the dispatch endpoint — same as SendRequestAsync but we discard the response
        // (replay is fire-and-forget, no caller waiting for the result).
        using var request = new HttpRequestMessage(HttpMethod.Post, basePath + "/dispatch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(envelope.Payload, ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("X-Conduit-Type", envelope.Type);
        request.Headers.Add("X-Conduit-Message-Id", Guid.NewGuid().ToString("N"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var request = new HttpRequestMessage(HttpMethod.Post, basePath + "/dispatch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(envelope.Payload, ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("X-Conduit-Type", envelope.Type);

        // Forwarded only when all three ids are plain tokens, as a trusting edge would honor them: a broker can hand this
        // host any id (a foreign producer's, CR/LF, kilobytes of it), and a header value with CR/LF throws FormatException
        // here. Otherwise none is sent and the receiver mints.
        var context = MessageContext.Current;
        if (context is not null
            && MessageContextIds.IsValid(context.MessageId)
            && MessageContextIds.IsValid(context.ConversationId)
            && MessageContextIds.IsValid(context.CausationId))
        {
            request.Headers.Add("X-Conduit-Message-Id", context.MessageId);
            request.Headers.Add("X-Conduit-Conversation-Id", context.ConversationId);
            request.Headers.Add("X-Conduit-Causation-Id", context.CausationId);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);

        // Read the body first to extract structured error info from DispatchResult,
        // then fall back to EnsureSuccessStatusCode for non-Conduit errors (503, network, etc.)
        var result = await response.Content.ReadFromJsonAsync<DispatchResult<JsonElement?>>(cancellationToken);
        var data = ConduitRuntime.UnwrapReply(result);

        response.EnsureSuccessStatusCode();

        return data;
    }
}
