using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Transport.Http;

/// <summary>
/// Sends messages to a remote Conduit endpoint via HTTP POST (<c>MapConduitEndpoint</c>). Used for C#-to-C# service
/// communication. The message type is sent in the <c>X-Conduit-Type</c> header and the body contains the plain
/// serialized message.
/// </summary>
/// <param name="httpClient">
/// The client to send with, its <c>BaseAddress</c> the remote host. The transport holds it for its own lifetime and never
/// disposes it: the caller (or <c>IHttpClientFactory</c>, for <c>UseHttp(configure?)</c>) owns it.
/// </param>
/// <param name="basePath">The path the remote host maps its Conduit endpoint on.</param>
/// <param name="typeRegistry">
/// This host's message types, which tell an event from a command in <see cref="SendEventAsync"/> and
/// <see cref="RedeliverAsync"/>. The <c>UseHttp</c> extensions pass the host's; without one, every such send is a command.
/// </param>
public class HttpTransport(
    HttpClient httpClient,
    string basePath = "/api/conduit",
    ConduitTypeRegistry? typeRegistry = null) : IConduitTransport
{
    /// <summary>
    /// Sends a message that expects no reply: an event to <c>{basePath}/publish</c>, anything else (a routed
    /// <c>[FireAndForget]</c> command, the only one that reaches here) to <c>{basePath}/dispatch</c>, its reply discarded.
    /// A failure is still the caller's: a refusal throws as <see cref="SendRequestAsync"/>'s does, any other non-success
    /// status as <see cref="HttpRequestException"/>.
    /// </summary>
    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var request = this.BuildRequest(this.PathFor(envelope.Type), envelope);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ReadReplyAsync(response, expectReply: false, cancellationToken);
    }

    /// <summary>
    /// Replays a message, its reply discarded (no caller is waiting), exactly as <see cref="SendEventAsync"/> sends it:
    /// with the current <see cref="MessageContext"/>'s ids, all three or none, never a lone message id a receiving edge
    /// would ignore; never <paramref name="ids"/>: only broker transports capture dead letters, so a dead-letter replay
    /// never comes here.
    /// </summary>
    /// <inheritdoc />
    public Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken)
        => this.SendEventAsync(envelope, cancellationToken);

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var request = this.BuildRequest("/dispatch", envelope);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadReplyAsync(response, expectReply: true, cancellationToken);
    }

    /// <summary>
    /// Reads the remote endpoint's answer. A Conduit endpoint answers <c>application/json</c>, success or refusal, so
    /// only that is read as a <see cref="DispatchResult{T}"/> (a refusal throws <see cref="RemoteDispatchException"/>).
    /// Anything else came from something in front of it — a reverse proxy's 502 page, an authentication middleware's
    /// bare 401 — and is reported by its status (<see cref="HttpRequestException"/>), never as a JSON parse failure.
    /// </summary>
    private static async Task<object?> ReadReplyAsync(
        HttpResponseMessage response,
        bool expectReply,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            response.EnsureSuccessStatusCode();

            // A success with no DispatchResult is what /publish answers, but a request's answer must carry one: a 200
            // HTML page from a fallback route is not a reply, and reading it as null would succeed silently.
            if (expectReply)
            {
                throw new HttpRequestException(
                    $"The remote endpoint answered {(int)response.StatusCode} with content type '{mediaType ?? "(none)"}', " +
                    "not a Conduit DispatchResult. Is the base path right?",
                    inner: null,
                    response.StatusCode);
            }

            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<DispatchResult<JsonElement?>>(cancellationToken);
        var data = ConduitRuntime.UnwrapReply(result);

        response.EnsureSuccessStatusCode();

        return data;
    }

    // An event is published; anything else is dispatched.
    private string PathFor(string type) =>
        typeRegistry is not null && typeRegistry.EventTypeNames.Contains(type) ? "/publish" : "/dispatch";

    private HttpRequestMessage BuildRequest(string path, DispatchEnvelope envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, basePath + path)
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

        return request;
    }
}
