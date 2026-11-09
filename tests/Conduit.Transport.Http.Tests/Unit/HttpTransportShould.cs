using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Transport.Http.Tests.Support;

namespace Conduit.Transport.Http.Tests.Unit;

[TestClass]
public class HttpTransportShould
{
    private static readonly string[] CorrelationHeaders =
        ["X-Conduit-Message-Id", "X-Conduit-Conversation-Id", "X-Conduit-Causation-Id"];

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task ThrowOnSendEvent()
    {
        // Arrange
        var httpClient = new HttpClient();
        var transport = new HttpTransport(httpClient);
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));

        // Act
        var action = () => transport.SendEventAsync(envelope, CancellationToken.None);

        // Assert
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

    [TestMethod]
    public async Task ForwardAContextWhoseIdsArePlainTokens()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://remote") };
        var transport = new HttpTransport(httpClient);
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));
        MessageContext.StartDispatchFromTransport("m-1", "p-1", "c-1");

        // Act
        await transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert
        var headers = handler.RequestHeaders.Should().ContainSingle().Subject;
        headers["X-Conduit-Message-Id"].Should().Be("m-1");
        headers["X-Conduit-Conversation-Id"].Should().Be("c-1");
        headers["X-Conduit-Causation-Id"].Should().Be("p-1");
    }

    // A broker can hand this host any id -- a foreign producer's, one with CR/LF, kilobytes of it -- and the handler may
    // route a command onward over HTTP. The ids are forwarded only when all three are plain tokens, as a trusting edge
    // would honor them; otherwise none is sent and the receiver mints, so the send neither throws nor carries junk.
    [TestMethod]
    public async Task NotForwardAContextThatIsNotPlainTokens()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://remote") };
        var transport = new HttpTransport(httpClient);
        var envelope = new DispatchEnvelope("Test", JsonSerializer.SerializeToElement(new { }));
        (string MessageId, string CausationId, string ConversationId)[] contexts =
        [
            ("m-1\r\nX-Injected: 1", "p-1", "c-1"),
            ("m-1", "p-1", new string('c', 65)),
            ("m-1", "p-é", "c-1"),
            ("ID:broker-host-1:1:1:1", "p-1", "c-1"),
        ];

        // Act
        foreach (var (messageId, causationId, conversationId) in contexts)
        {
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId);
            await transport.SendRequestAsync(envelope, CancellationToken.None);
        }

        // Assert
        handler.RequestHeaders.Should().HaveCount(contexts.Length);
        handler.RequestHeaders.Should().AllSatisfy(headers =>
        {
            headers.Should().ContainKey("X-Conduit-Type");
            headers.Keys.Should().NotContain(CorrelationHeaders);
        });
    }
}
