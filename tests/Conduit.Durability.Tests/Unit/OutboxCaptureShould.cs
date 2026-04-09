using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Runtime;
using Conduit.Durability.Store;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class OutboxCaptureShould
{
    private static TransportDispatcher CreateDispatcher(params IConduitTransport[] transports)
    {
        return new TransportDispatcher(
            new ConduitOptions(),
            new ServiceCollection().BuildServiceProvider(),
            transports);
    }

    private static OutboxCapture CreateCapture(StubOutboxStore? store = null, TransportDispatcher? dispatcher = null)
    {
        return new OutboxCapture(
            store ?? new StubOutboxStore(),
            dispatcher ?? CreateDispatcher(),
            NullLogger<OutboxCapture>.Instance);
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
        var capture = CreateCapture(store, CreateDispatcher(transport));

        // Act
        await capture.FlushAsync(CancellationToken.None);

        // Assert
        store.MarkedSent.Should().BeEmpty();
        transport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FlushAsyncSendClaimedEntriesAndMarkSent()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var capture = CreateCapture(store, CreateDispatcher(transport));

        var envelope1 = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var envelope2 = new DispatchEnvelope("MyApp.Events.OrderShipped", JsonSerializer.SerializeToElement(new { orderId = 2 }));

        capture.SetWrittenMessages([
            new WrittenOutboxMessage("msg-1", "conv-1", null, envelope1),
            new WrittenOutboxMessage("msg-2", "conv-2", null, envelope2),
        ]);

        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
            new ClaimedOutboxEntry { Id = 11, MessageId = "msg-2" },
        ];

        // Act
        await capture.FlushAsync(CancellationToken.None);

        // Assert
        transport.SentEvents.Should().HaveCount(2);
        transport.SentEvents[0].Type.Should().Be("MyApp.Events.OrderPlaced");
        transport.SentEvents[1].Type.Should().Be("MyApp.Events.OrderShipped");
        store.MarkedSent.Should().BeEquivalentTo([10L, 11L]);
    }

    [TestMethod]
    public async Task FlushAsyncIncrementRetryOnTransportFailure()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new ThrowingTransport();
        var capture = CreateCapture(store, CreateDispatcher(transport));

        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));

        capture.SetWrittenMessages([
            new WrittenOutboxMessage("msg-1", "conv-1", null, envelope),
        ]);

        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
        ];

        // Act
        await capture.FlushAsync(CancellationToken.None);

        // Assert
        store.MarkedSent.Should().BeEmpty();
        store.RetriesIncremented.Should().BeEquivalentTo([10L]);
    }

    [TestMethod]
    public async Task FlushAsyncSkipEntriesAlreadyClaimedByRelay()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var capture = CreateCapture(store, CreateDispatcher(transport));

        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));

        capture.SetWrittenMessages([
            new WrittenOutboxMessage("msg-1", "conv-1", null, envelope),
        ]);

        store.ClaimedEntries = [];

        // Act
        await capture.FlushAsync(CancellationToken.None);

        // Assert
        transport.SentEvents.Should().BeEmpty();
        store.MarkedSent.Should().BeEmpty();
    }
}
