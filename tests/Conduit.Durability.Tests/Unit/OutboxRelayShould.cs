using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class OutboxRelayShould
{
    private static OutboxEntry MakeEntry(long id, string type, int retryCount = 0)
        => new()
        {
            Id = id,
            MessageId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            CausationId = null,
            Type = type,
            Payload = """{}""",
            RetryCount = retryCount,
        };

    private static TransportDispatcher CreateDispatcher(params IConduitTransport[] transports)
    {
        return new TransportDispatcher(
            new ConduitOptions(),
            new ServiceCollection().BuildServiceProvider(),
            transports);
    }

    private static OutboxRelay BuildRelay(
        IOutboxStore store,
        TransportDispatcher dispatcher,
        OutboxOptions? outboxOptions = null,
        ILogger<OutboxRelay>? logger = null)
    {
        // OutboxRelay resolves IOutboxStore scope-per-sweep (see its class remarks), so tests wire
        // the stub store through a real IServiceScopeFactory rather than constructor-injecting it.
        var services = new ServiceCollection();
        services.AddScoped<IOutboxStore>(_ => store);
        var provider = services.BuildServiceProvider();

        return new OutboxRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            Options.Create(outboxOptions ?? new OutboxOptions { MaxRetries = 3 }),
            logger ?? NullLogger<OutboxRelay>.Instance);
    }

    [TestMethod]
    public async Task LogExhaustionAtAStableEventId_AndCountIt()
    {
        // Arrange -- the alert keys on EventId 320 or the counter; neither may drift.
        // The Meter is process-global and tests run in parallel, so the counter is filtered to this test's own type.
        const string type = "Orders.Events.OrderCreated.ExhaustionCounter";
        var entry = MakeEntry(id: 7, type: type, retryCount: 2);
        var store = new CapturingStubOutboxStore([entry]);
        var logger = new EventCapturingLogger();
        var relay = BuildRelay(store, CreateDispatcher(new ThrowingTransport(new InvalidOperationException("down"))),
            new OutboxOptions { MaxRetries = 3 }, logger);
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == "Conduit" && i.Name == "conduit.outbox.retries_exhausted")
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "conduit.message.type" && (string?)tag.Value == type)
                {
                    Interlocked.Add(ref counted, value);
                }
            }
        });
        listener.Start();

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        var exhausted = logger.Entries.Should().ContainSingle(e => e.Id.Id == 320).Which;
        exhausted.Id.Name.Should().Be("OutboxRetriesExhausted");
        exhausted.Level.Should().Be(LogLevel.Error);
        exhausted.Message.Should().Contain(type).And.Contain(entry.MessageId);
        Interlocked.Read(ref counted).Should().Be(1);
    }

    [TestMethod]
    public async Task FailedRetention_DefaultsToCompletedRetention()
    {
        var store = new StubOutboxStore();
        var relay = BuildRelay(store, CreateDispatcher(), new OutboxOptions { CompletedRetention = TimeSpan.FromDays(3) });

        await relay.CleanupCompletedAsync(CancellationToken.None);

        store.DeletedRetentions.Should().ContainSingle().Which.Should().Be(TimeSpan.FromDays(3));
        store.DeletedFailedRetentions.Should().ContainSingle().Which.Should().Be(TimeSpan.FromDays(3));
    }

    [TestMethod]
    public async Task FailedRetentionZero_KeepsFailedRowsForever()
    {
        var store = new StubOutboxStore();
        var relay = BuildRelay(store, CreateDispatcher(),
            new OutboxOptions { CompletedRetention = TimeSpan.FromDays(7), FailedRetention = TimeSpan.Zero });

        await relay.CleanupCompletedAsync(CancellationToken.None);

        store.DeletedRetentions.Should().ContainSingle().Which.Should().Be(TimeSpan.FromDays(7));
        store.DeletedFailedRetentions.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SendClaimedEntriesToTransport()
    {
        // Arrange
        var entry = MakeEntry(id: 1, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
        var transport = new CapturingTransport();
        var relay = BuildRelay(store, CreateDispatcher(transport));

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        transport.SentEventEnvelopes.Should().HaveCount(1);
        transport.SentEventEnvelopes[0].Type.Should().Be("Orders.Events.OrderCreated");
        store.MarkedSent.Should().ContainSingle().Which.Should().Be(1L);
        store.RetriesIncremented.Should().BeEmpty();
        store.MarkedFailed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task MarkEntryAsFailedAfterMaxRetries()
    {
        // Arrange — MaxRetries = 3, entry is at retryCount = 2 (next attempt would be #3 = max)
        var entry = MakeEntry(id: 2, type: "Orders.Events.OrderCreated", retryCount: 2);
        var store = new CapturingStubOutboxStore([entry]);
        var transport = new ThrowingTransport(new InvalidOperationException("broker down"));
        var relay = BuildRelay(store, CreateDispatcher(transport), new OutboxOptions { MaxRetries = 3 });

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        store.MarkedFailed.Should().ContainSingle().Which.Should().Be(2L);
        store.RetriesIncremented.Should().BeEmpty();
        store.MarkedSent.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IncrementRetryWhenUnderMaxRetries()
    {
        // Arrange — MaxRetries = 3, entry is at retryCount = 0 (room to retry)
        var entry = MakeEntry(id: 3, type: "Orders.Events.OrderCreated", retryCount: 0);
        var store = new CapturingStubOutboxStore([entry]);
        var transport = new ThrowingTransport(new InvalidOperationException("broker down"));
        var relay = BuildRelay(store, CreateDispatcher(transport), new OutboxOptions { MaxRetries = 3 });

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        store.RetriesIncremented.Should().ContainSingle().Which.Should().Be(3L);
        store.MarkedFailed.Should().BeEmpty();
        store.MarkedSent.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DoNothingWhenNoPendingEntries()
    {
        // Arrange
        var store = new CapturingStubOutboxStore([]);
        var transport = new CapturingTransport();
        var relay = BuildRelay(store, CreateDispatcher(transport));

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        transport.SentEventEnvelopes.Should().BeEmpty();
        store.MarkedSent.Should().BeEmpty();
        store.RetriesIncremented.Should().BeEmpty();
        store.MarkedFailed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IncrementRetryWhenALocalListenerThrows()
    {
        // Arrange — a claimed entry of a DeliversLocally type; the relay resolves both the stub
        // store and a throwing ILocalEnvelopeDispatcher from the same scope factory, so this test
        // builds the relay directly rather than through BuildRelay (which only wires the store).
        var entry = MakeEntry(id: 4, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
        var transport = new CapturingTransport();

        var services = new ServiceCollection();
        services.AddScoped<IOutboxStore>(_ => store);
        services.AddSingleton<ILocalEnvelopeDispatcher, ThrowingLocalDispatcher>();
        var provider = services.BuildServiceProvider();

        var relay = new OutboxRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            CreateDispatcher(transport),
            Options.Create(new OutboxOptions { MaxRetries = 3 }),
            NullLogger<OutboxRelay>.Instance);

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert — local dispatch failure is caught by the same retry path as a transport failure
        store.RetriesIncremented.Should().ContainSingle().Which.Should().Be(4L);
        store.MarkedSent.Should().BeEmpty();
        transport.SentEventEnvelopes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DeleteCompletedEntriesOnCleanup()
    {
        // Arrange
        var store = new StubOutboxStore();
        var outboxOptions = new OutboxOptions { CompletedRetention = TimeSpan.FromDays(3) };
        var relay = BuildRelay(store, CreateDispatcher(), outboxOptions);

        // Act
        await relay.CleanupCompletedAsync(CancellationToken.None);

        // Assert
        store.DeletedRetentions.Should().ContainSingle().Which.Should().Be(TimeSpan.FromDays(3));
    }
}

file sealed class CapturingStubOutboxStore(IReadOnlyList<OutboxEntry> pending) : StubOutboxStore
{
    public override Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
        => Task.FromResult(pending);
}

file sealed class CapturingTransport : IConduitTransport
{
    public List<DispatchEnvelope> SentEventEnvelopes { get; } = [];
    public List<DispatchEnvelope> SentRequestEnvelopes { get; } = [];

    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentRequestEnvelopes.Add(envelope);
        return Task.FromResult<object?>(null);
    }

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentEventEnvelopes.Add(envelope);
        return Task.CompletedTask;
    }

    public Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

file sealed class ThrowingTransport(Exception exception) : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;

    public Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;
}

/// <summary>Local dispatcher stub whose local delivery always throws, to exercise the relay's retry path.</summary>
file sealed class ThrowingLocalDispatcher : ILocalEnvelopeDispatcher
{
    public bool DeliversLocally(string messageType) => true;

    public bool IsLocalOnly(string messageType) => false;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated local dispatch failure");
}

file sealed class EventCapturingLogger : ILogger<OutboxRelay>
{
    public List<(LogLevel Level, EventId Id, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => this.Entries.Add((logLevel, eventId, formatter(state, exception)));
}
