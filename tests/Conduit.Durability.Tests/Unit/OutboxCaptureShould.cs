using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Persistence;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;
using System.Data.Common;
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

    private static IServiceScopeFactory CreateEmptyScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static OutboxCapture CreateCapture(
        StubOutboxStore? store = null,
        TransportDispatcher? dispatcher = null,
        IConduitConnectionFactory? connectionFactory = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        return new OutboxCapture(
            store ?? new StubOutboxStore(),
            dispatcher ?? CreateDispatcher(),
            connectionFactory ?? new TransactionPresentConnectionFactory(),
            scopeFactory ?? CreateEmptyScopeFactory(),
            NullLogger<OutboxCapture>.Instance);
    }

    [TestMethod]
    public void CaptureInterceptedEnvelopes()
    {
        // Arrange — ambient transaction present, so capture accepts the envelopes
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
    public void DeclineCaptureWhenNoAmbientTransaction()
    {
        // Arrange — factory with GetCurrentTransaction() == null
        var capture = CreateCapture(connectionFactory: new NoTransactionConnectionFactory());
        var envelope = new DispatchEnvelope("Test.Event", JsonSerializer.SerializeToElement(new { }));

        // Act
        var captured = capture.Intercept(envelope);

        // Assert — declined: OutboxBehavior could never persist this, so capture must decline,
        // leaving the caller to deliver via the pre-outbox path (no silent drop).
        captured.Should().BeFalse();
        capture.Captured.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CaptureWhenTransactionIsAmbient()
    {
        // Arrange — a real ambient transaction on the shared in-memory connection
        await using var factory = new StubConnectionFactory();
        await factory.BeginTransactionAsync();
        var capture = CreateCapture(connectionFactory: factory);
        var envelope = new DispatchEnvelope("Test.Event", JsonSerializer.SerializeToElement(new { }));

        // Act
        var captured = capture.Intercept(envelope);

        // Assert
        captured.Should().BeTrue();
        capture.Captured.Should().ContainSingle();
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
    public async Task FlushAsyncIncrementRetryOnLocalDispatchFailure()
    {
        // Arrange — local dispatcher throws; capture resolves it from the wired scope factory
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var localDispatcherServices = new ServiceCollection();
        localDispatcherServices.AddSingleton<ILocalEnvelopeDispatcher, ThrowingLocalDispatcher>();
        var scopeFactory = localDispatcherServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var capture = CreateCapture(store, CreateDispatcher(transport), scopeFactory: scopeFactory);

        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));

        capture.SetWrittenMessages([
            new WrittenOutboxMessage("msg-1", "conv-1", null, envelope),
        ]);

        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
        ];

        // Act
        var act = async () => await capture.FlushAsync(CancellationToken.None);

        // Assert — no exception escapes; entry is retried, never marked sent, transport never reached
        await act.Should().NotThrowAsync();
        store.RetriesIncremented.Should().BeEquivalentTo([10L]);
        store.MarkedSent.Should().BeEmpty();
        transport.SentEvents.Should().BeEmpty();
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

/// <summary>Connection factory stub that reports an ambient transaction, so capture accepts envelopes.</summary>
file sealed class TransactionPresentConnectionFactory : IConduitConnectionFactory
{
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public DbTransaction? GetCurrentTransaction() => new FakeDbTransaction();
}

/// <summary>Connection factory stub with no ambient transaction, so capture declines.</summary>
file sealed class NoTransactionConnectionFactory : IConduitConnectionFactory
{
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public DbTransaction? GetCurrentTransaction() => null;
}

file sealed class FakeDbTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection => null;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
    }
}

/// <summary>Local dispatcher stub whose local delivery always throws, to exercise flush's failure path.</summary>
file sealed class ThrowingLocalDispatcher : ILocalEnvelopeDispatcher
{
    public bool DeliversLocally(string messageType) => true;

    public bool IsLocalOnly(string messageType) => false;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated local dispatch failure");
}
