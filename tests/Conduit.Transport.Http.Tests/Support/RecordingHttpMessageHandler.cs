using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that records the headers of every request sent through it and answers each with
/// an empty successful <c>DispatchResult</c>, so a test can see exactly what <see cref="HttpTransport"/> put on the wire.
/// </summary>
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> requests = new();

    public IReadOnlyList<IReadOnlyDictionary<string, string>> RequestHeaders => this.requests.ToList();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.requests.Enqueue(request.Headers.ToDictionary(
            header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":null}", Encoding.UTF8, "application/json"),
        });
    }
}
