using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Persistence;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Data;
using System.Data.Common;
using System.Globalization;
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
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" },
            new ClaimedOutboxEntry { Id = 11, MessageId = "msg-2", ClaimToken = "token-11" },
        ];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert
        transport.SentEvents.Should().HaveCount(2);
        transport.SentEvents[0].Type.Should().Be("MyApp.Events.OrderPlaced");
        transport.SentEvents[1].Type.Should().Be("MyApp.Events.OrderShipped");
        store.MarkedSent.Should().BeEquivalentTo([10L, 11L]);
        store.ResolvedClaimTokens.Should().Equal("token-10", "token-11");
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
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" },
        ];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert -- with no options registered, the default first backoff step
        store.MarkedSent.Should().BeEmpty();
        store.RetriesIncremented.Should().BeEquivalentTo([10L]);
        store.RetryDelays.Should().Equal(TimeSpan.FromSeconds(5));
        store.ResolvedClaimTokens.Should().Equal("token-10");
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncLogALostClaimWhenMarkingSent()
    {
        // Arrange -- the delivery outlasted the claim timeout and the relay re-claimed the entry
        var store = new StubOutboxStore { ClaimLost = true };
        using var spy = new SpyLoggerProvider();
        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var messages = new List<WrittenOutboxMessage> { new("msg-1", "conv-1", null, envelope) };
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" }];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(new SpyTransport()), spy.CreateLogger("outbox"), messages, CancellationToken.None);

        // Assert -- the entry belongs to the relay's claim now: no retry is recorded on top of it
        AssertClaimLostLogged(spy, entryId: 10, type: "MyApp.Events.OrderPlaced");
        store.MarkedSent.Should().Equal(10L);
        store.RetriesIncremented.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncLogALostClaimWhenReleasingAFailedSend()
    {
        // Arrange
        var store = new StubOutboxStore { ClaimLost = true };
        using var spy = new SpyLoggerProvider();
        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var messages = new List<WrittenOutboxMessage> { new("msg-1", "conv-1", null, envelope) };
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" }];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(new ThrowingTransport()), spy.CreateLogger("outbox"), messages, CancellationToken.None);

        // Assert
        AssertClaimLostLogged(spy, entryId: 10, type: "MyApp.Events.OrderPlaced");
        store.RetriesIncremented.Should().Equal(10L);
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncSurviveAnAsyncOnlyScopedService()
    {
        // Arrange -- the entry's listener, and the flush's store, each resolve a scoped service that implements only
        // IAsyncDisposable, which IServiceScope.Dispose() refuses with an exception
        var store = new StubOutboxStore();
        var created = new List<AsyncOnlyScopedService>();
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var service = new AsyncOnlyScopedService();
            created.Add(service);
            return service;
        });
        services.AddScoped<IOutboxStore>(serviceProvider =>
        {
            serviceProvider.GetRequiredService<AsyncOnlyScopedService>();
            return store;
        });
        services.AddSingleton<ILocalEnvelopeDispatcher>(new ScopedServiceListener<AsyncOnlyScopedService>());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var messages = new List<WrittenOutboxMessage> { new("msg-1", "conv-1", null, envelope) };
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" }];

        // Act
        var act = () => OutboxCapture.ClaimAndDeliverAsync(
            scopeFactory, CreateDispatcher(), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert -- marked sent and left sent, not released for the relay to deliver again
        await act.Should().NotThrowAsync();
        store.MarkedSent.Should().Equal(10L);
        store.RetriesIncremented.Should().BeEmpty();
        created.Should().HaveCount(2).And.OnlyContain(service => service.Disposed);
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncKeepAnEntrySentWhenItsScopeFailsToDispose()
    {
        // Arrange -- the entry's listener resolves a scoped service whose DisposeAsync throws
        var store = new StubOutboxStore();
        using var spy = new SpyLoggerProvider();
        var scopeFactory = CreateScopeFactoryWithListener(store, new ScopedServiceListener<ThrowingDisposeScopedService>());
        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var messages = new List<WrittenOutboxMessage> { new("msg-1", "conv-1", null, envelope) };
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" }];

        // Act
        var act = () => OutboxCapture.ClaimAndDeliverAsync(
            scopeFactory, CreateDispatcher(), spy.CreateLogger("outbox"), messages, CancellationToken.None);

        // Assert -- marking it sent was the point of no return: the disposal failure is logged, never retried
        await act.Should().NotThrowAsync();
        store.MarkedSent.Should().Equal(10L);
        store.RetriesIncremented.Should().BeEmpty();
        AssertScopeDisposeFailureLogged(spy, entryId: 10, type: "MyApp.Events.OrderPlaced");
        spy.Entries.Should().NotContain(e => e.EventId.Id == 321);
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncLogTheListenersFailureNotItsScopesDisposalFailure()
    {
        // Arrange -- the listener throws, and so does disposing the scope it ran in
        var store = new StubOutboxStore();
        var listenerFailure = new InvalidOperationException("Simulated listener failure");
        using var spy = new SpyLoggerProvider();
        var scopeFactory = CreateScopeFactoryWithListener(
            store, new ScopedServiceListener<ThrowingDisposeScopedService>(listenerFailure));
        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var messages = new List<WrittenOutboxMessage> { new("msg-1", "conv-1", null, envelope) };
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" }];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            scopeFactory, CreateDispatcher(), spy.CreateLogger("outbox"), messages, CancellationToken.None);

        // Assert -- the flush failure names the listener's exception; the disposal failure is logged on its own
        spy.Entries.Should().ContainSingle(e => e.Message.StartsWith("Failed to flush outbox entry", StringComparison.Ordinal))
            .Which.Exception.Should().BeSameAs(listenerFailure);
        store.RetriesIncremented.Should().Equal(10L);
        AssertScopeDisposeFailureLogged(spy, entryId: 10, type: "MyApp.Events.OrderPlaced");
    }

    [TestMethod]
    public async Task ClaimAndDeliverAsyncBackOffAFailedEntryByTheInitialBackoff()
    {
        // Arrange -- a flush is an entry's first attempt, so its failure waits the first backoff step
        var store = new StubOutboxStore();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton(Options.Create(new OutboxOptions { InitialBackoff = TimeSpan.FromSeconds(7) }));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var envelope = new DispatchEnvelope("MyApp.Events.OrderPlaced", JsonSerializer.SerializeToElement(new { orderId = 1 }));
        var messages = new List<WrittenOutboxMessage>
        {
            new("msg-1", "conv-1", null, envelope),
        };
        store.ClaimedEntries =
        [
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" },
        ];

        // Act
        await OutboxCapture.ClaimAndDeliverAsync(
            scopeFactory, CreateDispatcher(new ThrowingTransport()), NullLogger<OutboxCapture>.Instance, messages, CancellationToken.None);

        // Assert
        store.RetryDelays.Should().Equal(TimeSpan.FromSeconds(7));
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
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" },
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
            new ClaimedOutboxEntry { Id = 10, MessageId = "msg-1", ClaimToken = "token-10" },
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

    // The store, the listener, and a scoped ThrowingDisposeScopedService for the listener to resolve.
    private static IServiceScopeFactory CreateScopeFactoryWithListener(IOutboxStore store, ILocalEnvelopeDispatcher listener)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(listener);
        services.AddScoped<ThrowingDisposeScopedService>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static void AssertScopeDisposeFailureLogged(SpyLoggerProvider spy, long entryId, string type)
    {
        var disposeFailed = spy.Entries.Should().ContainSingle(e => e.EventId.Id == 322).Which;
        disposeFailed.EventId.Name.Should().Be("OutboxScopeDisposeFailed");
        disposeFailed.Level.Should().Be(LogLevel.Warning);
        disposeFailed.Message.Should().Contain(entryId.ToString(CultureInfo.InvariantCulture)).And.Contain(type);
        disposeFailed.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Simulated scope disposal failure");
    }

    private static void AssertClaimLostLogged(SpyLoggerProvider spy, long entryId, string type)
    {
        var claimLost = spy.Entries.Should().ContainSingle(e => e.EventId.Id == 321).Which;
        claimLost.EventId.Name.Should().Be("ClaimLost");
        claimLost.Level.Should().Be(LogLevel.Warning);
        claimLost.Message.Should().Contain(entryId.ToString(CultureInfo.InvariantCulture)).And.Contain(type);
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
