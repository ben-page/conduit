using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.Transport.Http;

namespace Conduit.Transport.Tests;

[TestClass]
public class HttpTransportTests
{
    [TestMethod]
    public async Task SendMessageAsync_ThrowsNotSupported()
    {
        var httpClient = new HttpClient();
        var transport = new HttpTransport(httpClient);
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));

        var action = () => transport.SendMessageAsync(envelope, CancellationToken.None);

        await action.Should().ThrowExactlyAsync<NotSupportedException>();
    }
}
