using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;

namespace Conduit.Transport.Http.Tests.Unit;

[TestClass]
public class HttpTransportShould
{
    [TestMethod]
    public async Task ThrowOnSendEvent()
    {
        var httpClient = new HttpClient();
        var transport = new HttpTransport(httpClient);
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));

        var action = () => transport.SendEventAsync(envelope, CancellationToken.None);

        await action.Should().ThrowExactlyAsync<NotSupportedException>();
    }

    [TestMethod]
    public async Task RejectNullEnvelope()
    {
        // Arrange
        var httpClient = new HttpClient();
        var transport = new HttpTransport(httpClient);

        // Act
        var action = () => transport.SendRequestAsync(null!, CancellationToken.None);

        // Assert
        await action.Should().ThrowExactlyAsync<ArgumentNullException>();
    }
}
