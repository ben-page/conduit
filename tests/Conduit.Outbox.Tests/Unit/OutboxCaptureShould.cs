using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Outbox.Runtime;
using Conduit.Outbox.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Conduit.Outbox.Tests.Unit;

[TestClass]
public class OutboxCaptureShould
{
    private static OutboxCapture CreateCapture(StubOutboxStore? store = null, IConduitTransport[]? transports = null)
    {
        return new OutboxCapture(
            store ?? new StubOutboxStore(),
            transports ?? [],
            new ServiceCollection().BuildServiceProvider(),
            new ConduitOptions());
    }

    [TestMethod]
    public void CaptureInterceptedEnvelopes()
    {
        // Arrange
        var capture = CreateCapture();
        var envelope1 = new DispatchEnvelope("Test.Event1", JsonSerializer.SerializeToElement(new { }));
        var envelope2 = new DispatchEnvelope("Test.Event2", JsonSerializer.SerializeToElement(new { }));

        // Act
        capture.Intercept(envelope1);
        capture.Intercept(envelope2);

        // Assert
        capture.Captured.Should().HaveCount(2);
        capture.Captured[0].Type.Should().Be("Test.Event1");
        capture.Captured[1].Type.Should().Be("Test.Event2");
    }

    [TestMethod]
    public void StartEmpty()
    {
        // Arrange & Act
        var capture = CreateCapture();

        // Assert
        capture.Captured.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FlushAsyncNoOpWhenNothingWritten()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var capture = CreateCapture(store, [transport]);

        // Act
        await capture.FlushAsync(CancellationToken.None);

        // Assert
        store.MarkedSent.Should().BeEmpty();
        transport.SentEvents.Should().BeEmpty();
    }
}
