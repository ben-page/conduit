using System.Net;
using System.Text;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request with the same status and body, as something other
/// than a Conduit endpoint would: a reverse proxy's 502 page, an authentication middleware's bare 401.
/// </summary>
public sealed class StaticResponseHttpMessageHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        });
}
