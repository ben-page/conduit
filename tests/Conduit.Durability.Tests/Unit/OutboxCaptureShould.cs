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

    // ClaimAndDeliverAsync resolves IOutboxStore (and, when present, ILocalEnvelopeDispatcher) from
    // its own fresh scope rather than taking them as parameters directly — this wires the given
    // stubs into a real scope factory so the routine under test can resolve them, mirroring how
    // production DI provides them from the request/relay scope factory.
    private static IServiceScopeFactory CreateScopeFactory(IOutboxStore store, ILocalEnvelopeDispatcher? localDispatcher = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        if (localDispatcher is not null)
        {
            services.AddSingleton(localDispatcher);
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // A real (but never-started) dispatcher: fine for tests that only assert whether FlushAsync
    // enqueues, never for tests that need the enqueued item to actually drain — see
    // ClaimAndDeliverAsync-targeted tests below, which invoke the delivery routine directly instead.
    private static ConduitBackgroundDispatcher CreateBackgroundDispatcher()
        => new(NullLogger<ConduitBackgroundDispatcher>.Instance);

    private static OutboxCapture CreateCapture(
        TransportDispatcher? dispatcher = null,
        IConduitConnectionFactory? connectionFactory = null,
        IServiceScopeFactory? scopeFactory = null,
        ConduitBackgroundDispatcher? backgroundDispatcher = null)
    {
        return new OutboxCapture(
            dispatcher ?? CreateDispatcher(),
            connectionFactory ?? new TransactionPresentConnectionFactory(),
            scopeFactory ?? CreateEmptyScopeFactory(),
            backgroundDispatcher ?? CreateBackgroundDispatcher(),
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
        // Arrange — store/transport are wired into capture's own scope factory (the same one
        // FlushAsync would hand the delivery routine, were it to run) so the assertions below
        // would catch a regression to the old inline-delivery behavior.
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var capture = CreateCapture(CreateDispatcher(transport), scopeFactory: CreateScopeFactory(store));

        // Act
        await capture.FlushAsync(CancellationToken.None);

        // Assert — nothing captured means nothing is scheduled: the delivery routine never runs.
        // This is a deliberately weak signal at the unit level — ConduitBackgroundDispatcher is
        // sealed with a non-virtual Enqueue so it can't be spied, and this dispatcher is never
        // started, so "enqueued but never drained" and "never enqueued" both look like this. What
        // it DOES pin is the empty-buffer early return itself: a regression back to claiming
        // synchronously (the pre-background-flush shape) would show up here immediately. The
        // "non-empty enqueues exactly once" half of the contract is pinned behaviorally by the
        // integration suite instead (see
        // OutboxLocalDeliveryShould.DeliverLocalListenersExactlyOnceOnFlushThenTransportsThenMarkSent).
        store.ClaimByMessageIdsCalled.Should().BeFalse();
        store.MarkedSent.Should().BeEmpty();
        transport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncSendClaimedEntriesAndMarkSent()
    {
        // Arrange — exercises the extracted delivery routine directly: this is the same
        // claim-then-send-then-mark-sent loop FlushAsync used to run inline and now schedules on
        // the background dispatcher instead, so it's pinned here rather than through FlushAsync.
        var store = new StubOutboxStore();
        var transport = new SpyTransport();

        var envelope1 = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var envelope2 = new DispatchEnvelope("MyApp.Events.OrderShipped", JsonSerializer.SerializeToElement(new { orderId = 2 }));

        var messages = new List<WrittenOutboxMessage>
        {
            new("msg-1", "conv-1", null, envelope1),
            new("msg-2", "conv-2", null, envelope2),
        };

        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
            new ClaimedOutboxEntry { Id = 11, MessageId = "msg-2" },
        ];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert
        transport.SentEvents.Should().HaveCount(2);
        transport.SentEvents[0].Type.Should().Be("MyApp.Events.OrderPlaced");
        transport.SentEvents[1].Type.Should().Be("MyApp.Events.OrderShipped");
        store.MarkedSent.Should().BeEquivalentTo([10L, 11L]);
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncIncrementRetryOnTransportFailure()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new ThrowingTransport();

        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));

        var messages = new List<WrittenOutboxMessage>
        {
            new("msg-1", "conv-1", null, envelope),
        };

        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
        ];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert
        store.MarkedSent.Should().BeEmpty();
        store.RetriesIncremented.Should().BeEquivalentTo([10L]);
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncIncrementRetryOnLocalDispatchFailure()
    {
        // Arrange — local dispatcher throws; the routine resolves both it and the store from the
        // wired scope factory
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var scopeFactory = CreateScopeFactory(store, new ThrowingLocalDispatcher());

        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));

        var messages = new List<WrittenOutboxMessage>
        {
            new("msg-1", "conv-1", null, envelope),
        };

        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
        ];

        // Act
        var act = async () => await OutboxCapture.ClaimAndDeliverAsync(
            scopeFactory, CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert — no exception escapes; entry is retried, never marked sent, transport never reached
        await act.Should().NotThrowAsync();
        store.RetriesIncremented.Should().BeEquivalentTo([10L]);
        store.MarkedSent.Should().BeEmpty();
        transport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncSendARoutedFireAndForgetCommandWithoutAwaitingAReply()
    {
        // Arrange -- a captured [FireAndForget] command routed to another service, classified by the flush's own scope
        const string type = "Billing.Commands.RecordCharge";
        var store = new StubOutboxStore();
        var billing = new SpyTransport();
        var options = new ConduitOptions();
        options.Endpoints["billing"] = new ConduitEndpoint { Name = "billing" };
        options.Routes.Add(new ConduitRoute("Billing.Commands", "billing", Exact: false));
        var transports = new ServiceCollection();
        transports.AddKeyedSingleton<IConduitTransport>("billing", billing);
        var dispatcher = new TransportDispatcher(options, transports.BuildServiceProvider(), []);
        var envelope = new DispatchEnvelope(type, JsonSerializer.SerializeToElement(new { amount = 5 }));
        var messages = new List<WrittenOutboxMessage>
        {
            new("msg-1", "conv-1", null, envelope),
        };
        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1" },
        ];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store, new FireAndForgetClassifier(type)), dispatcher, NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert -- sent with no reply awaited, as the publish-time path sends it
        billing.SentEvents.Should().ContainSingle().Which.Type.Should().Be(type);
        billing.SentRequests.Should().BeEmpty();
        store.MarkedSent.Should().BeEquivalentTo([10L]);
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncSkipEntriesAlreadyClaimedByRelay()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new SpyTransport();

        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));

        var messages = new List<WrittenOutboxMessage>
        {
            new("msg-1", "conv-1", null, envelope),
        };

        store.ClaimedEntries = [];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

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

    public bool IsFireAndForget(string fullTypeName) => false;

    public bool IsEvent(string fullTypeName) => false;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated local dispatch failure");
}
