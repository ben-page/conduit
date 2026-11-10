using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.Outbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class OutboxRelayShould
{
    private static OutboxEntry MakeEntry(long id, string type, int retryCount = 0, string payload = "{}")
        => new()
        {
            Id = id,
            MessageId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            CausationId = null,
            Type = type,
            Payload = payload,
            RetryCount = retryCount,
            ClaimToken = $"token-{id}",
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
        ILogger<OutboxRelay>? logger = null,
        TimeProvider? timeProvider = null)
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
            logger ?? NullLogger<OutboxRelay>.Instance,
            timeProvider);
    }

    [TestMethod]
    public async Task CleanUpOnceTheInjectedClockPassesTheCleanupInterval()
    {
        // Arrange — the relay sweeps every few milliseconds, and its clock alone says when the hour has passed
        var store = new SweepCountingOutboxStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var relay = BuildRelay(
            store,
            CreateDispatcher(),
            new OutboxOptions { RelayInterval = TimeSpan.FromMilliseconds(5), CleanupInterval = TimeSpan.FromHours(1) },
            timeProvider: clock);

        // Act — sweeps while the clock stands still, then sweeps once it has moved the hour
        await relay.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => store.Sweeps >= 3);
        var cleanedWhileTheClockStood = store.DeletedRetentions.Count;
        clock.Advance(TimeSpan.FromHours(1));
        await WaitUntilAsync(() => store.DeletedRetentions.Count > 0);
        await relay.StopAsync(CancellationToken.None);

        // Assert — one cleanup: the clock moved one interval
        cleanedWhileTheClockStood.Should().Be(0);
        store.DeletedRetentions.Should().ContainSingle();
    }

    // Polls the relay's background loop for up to five seconds.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The relay never reached the expected state.");
            }

            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task RecordAZeroExhaustion_WhenItStarts_SoTheSeriesExistsBeforeAnyFailure()
    {
        // Arrange -- an OpenTelemetry counter exports nothing until it is first recorded; an alert needs the series.
        var relay = BuildRelay(new StubOutboxStore(), CreateDispatcher());
        var zeros = 0;
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
            if (value == 0 && tags.Length == 0)
            {
                Interlocked.Increment(ref zeros);
            }
        });
        listener.Start();

        // Act -- since .NET 10 BackgroundService runs ExecuteAsync on the thread pool, so wait for the zero
        // rather than stop at once (which can cancel the relay before it starts).
        await relay.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref zeros) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await relay.StopAsync(CancellationToken.None);

        // Assert
        Volatile.Read(ref zeros).Should().BeGreaterThan(0, "the relay records the untagged zero on start");
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
        store.ResolvedClaimTokens.Should().Equal("token-1");
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
        store.ResolvedClaimTokens.Should().Equal("token-2");
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
        store.ResolvedClaimTokens.Should().Equal("token-3");
        store.MarkedFailed.Should().BeEmpty();
        store.MarkedSent.Should().BeEmpty();
    }

    [TestMethod]
    public async Task LogALostClaim_WhenMarkingSent_AndRecordNothingElse()
    {
        // Arrange -- the send outlasted the claim timeout and another relay re-claimed the entry
        var entry = MakeEntry(id: 11, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]) { ClaimLost = true };
        var logger = new EventCapturingLogger();
        var relay = BuildRelay(store, CreateDispatcher(new CapturingTransport()), logger: logger);

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- the entry belongs to the other claim now: no retry is recorded on top of it
        AssertClaimLostLogged(logger.Entries, entry);
        store.MarkedSent.Should().Equal(11L);
        store.RetriesIncremented.Should().BeEmpty();
        store.MarkedFailed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task LogALostClaim_WhenReleasingAFailedSend()
    {
        // Arrange
        var entry = MakeEntry(id: 12, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]) { ClaimLost = true };
        var logger = new EventCapturingLogger();
        var relay = BuildRelay(
            store,
            CreateDispatcher(new ThrowingTransport(new InvalidOperationException("broker down"))),
            logger: logger);

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        AssertClaimLostLogged(logger.Entries, entry);
        store.RetriesIncremented.Should().Equal(12L);
        store.MarkedFailed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task LogALostClaimInsteadOfExhaustion_WhenMarkingFailed()
    {
        // Arrange -- the last attempt failed, but another relay holds the entry now and may yet deliver it.
        // The Meter is process-global and tests run in parallel, so the counter is filtered to this test's own type.
        const string type = "Orders.Events.OrderCreated.LostClaimCounter";
        var entry = MakeEntry(id: 13, type: type, retryCount: 2);
        var store = new CapturingStubOutboxStore([entry]) { ClaimLost = true };
        var logger = new EventCapturingLogger();
        var relay = BuildRelay(
            store,
            CreateDispatcher(new ThrowingTransport(new InvalidOperationException("broker down"))),
            new OutboxOptions { MaxRetries = 3 },
            logger);
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

        // Assert -- not reported as exhausted: the entry is not Failed
        AssertClaimLostLogged(logger.Entries, entry);
        store.MarkedFailed.Should().Equal(13L);
        logger.Entries.Should().NotContain(e => e.Id.Id == 320);
        Interlocked.Read(ref counted).Should().Be(0);
    }

    [TestMethod]
    public async Task SurviveAnAsyncOnlyScopedService()
    {
        // Arrange -- the entry's listener, and the sweep's store, each resolve a scoped service that implements only
        // IAsyncDisposable, which IServiceScope.Dispose() refuses with an exception
        var entry = MakeEntry(id: 14, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
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
        var provider = services.BuildServiceProvider();
        var relay = new OutboxRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            CreateDispatcher(),
            Options.Create(new OutboxOptions { MaxRetries = 3 }),
            NullLogger<OutboxRelay>.Instance);

        // Act
        var act = () => relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- marked sent and left sent, not flipped back to Pending by a disposal failure
        await act.Should().NotThrowAsync();
        store.MarkedSent.Should().Equal(14L);
        store.RetriesIncremented.Should().BeEmpty();
        created.Should().HaveCount(2).And.OnlyContain(service => service.Disposed);
    }

    [TestMethod]
    public async Task KeepAnEntrySent_WhenItsScopeFailsToDispose()
    {
        // Arrange -- the entry's listener resolves a scoped service whose DisposeAsync throws
        var entry = MakeEntry(id: 21, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
        using var spy = new SpyLoggerProvider();
        using var loggerFactory = new LoggerFactory([spy]);
        var relay = BuildRelayWithListener(
            store,
            new ScopedServiceListener<ThrowingDisposeScopedService>(),
            loggerFactory.CreateLogger<OutboxRelay>(),
            services => services.AddScoped<ThrowingDisposeScopedService>());

        // Act
        var act = () => relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- marking it sent was the point of no return: the disposal failure is logged, never retried
        await act.Should().NotThrowAsync();
        store.MarkedSent.Should().Equal(21L);
        store.RetriesIncremented.Should().BeEmpty();
        AssertScopeDisposeFailureLogged(spy, entry);
        spy.Entries.Should().NotContain(e => e.EventId.Id == 321);
    }

    [TestMethod]
    public async Task LogTheListenersFailure_NotItsScopesDisposalFailure()
    {
        // Arrange -- the listener throws, and so does disposing the scope it ran in
        var entry = MakeEntry(id: 22, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
        var listenerFailure = new InvalidOperationException("Simulated listener failure");
        using var spy = new SpyLoggerProvider();
        using var loggerFactory = new LoggerFactory([spy]);
        var relay = BuildRelayWithListener(
            store,
            new ScopedServiceListener<ThrowingDisposeScopedService>(listenerFailure),
            loggerFactory.CreateLogger<OutboxRelay>(),
            services => services.AddScoped<ThrowingDisposeScopedService>());

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- the send failure names the listener's exception; the disposal failure is logged on its own
        spy.Entries.Should().ContainSingle(e => e.Message.StartsWith("Failed to send outbox entry", StringComparison.Ordinal))
            .Which.Exception.Should().BeSameAs(listenerFailure);
        store.RetriesIncremented.Should().Equal(22L);
        AssertScopeDisposeFailureLogged(spy, entry);
    }

    [TestMethod]
    public async Task KeepAnEntrySent_WhenAnObserverFails()
    {
        // Arrange -- the listener attaches an observer that throws, so recording the entry as sent throws after the
        // entry was marked sent
        var entry = MakeEntry(id: 23, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
        using var spy = new SpyLoggerProvider();
        using var loggerFactory = new LoggerFactory([spy]);
        var relay = BuildRelayWithListener(
            store,
            new ObserverAttachingListener(new ThrowingObserver()),
            loggerFactory.CreateLogger<OutboxRelay>());

        // Act
        var act = () => relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- logged as an observation failure, not as a failed send or a lost claim, and never retried
        await act.Should().NotThrowAsync();
        store.MarkedSent.Should().Equal(23L);
        store.RetriesIncremented.Should().BeEmpty();
        spy.Entries.Should().NotContain(e => e.EventId.Id == 321);
        var observationFailed = spy.Entries.Should().ContainSingle(e => e.EventId.Id == 323).Which;
        observationFailed.EventId.Name.Should().Be("OutboxObservationFailed");
        observationFailed.Level.Should().Be(LogLevel.Warning);
        observationFailed.Message.Should().Contain("23").And.Contain(nameof(OutboxSent));
    }

    [TestMethod]
    public async Task NotCarryAnEntrysContextIntoTheNextEntry()
    {
        // Arrange -- the first entry is delivered under its restored context; the second fails to read before it
        // restores its own, so it is released under whatever context the relay leaves current
        var delivered = MakeEntry(id: 15, type: "Orders.Events.OrderCreated");
        var unreadable = MakeEntry(id: 16, type: "Orders.Events.OrderCreated", payload: "not json");
        var store = new ContextRecordingStubOutboxStore([delivered, unreadable]);
        var relay = BuildRelay(store, CreateDispatcher(new CapturingTransport()));
        var ambient = MessageContext.Current;

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        store.RetriesIncremented.Should().Equal(16L);
        store.ContextAtRetry.Should().BeSameAs(ambient, "the first entry's context ended with the first entry");
    }

    [TestMethod]
    public async Task RefuseAClaimedEntryWithoutItsClaimToken()
    {
        // Arrange -- a custom store that drops the token would otherwise have every outcome refused, silently
        var entry = new OutboxEntry
        {
            Id = 17,
            MessageId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            Type = "Orders.Events.OrderCreated",
            Payload = "{}",
        };
        var store = new CapturingStubOutboxStore([entry]);
        var transport = new CapturingTransport();
        var relay = BuildRelay(store, CreateDispatcher(transport));

        // Act
        var act = () => relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- the sweep fails loudly, before delivering anything
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ClaimToken");
        transport.SentEventEnvelopes.Should().BeEmpty();
    }

    // A relay whose entry scopes resolve the listener, plus whatever registerServices adds.
    private static OutboxRelay BuildRelayWithListener(
        IOutboxStore store,
        ILocalEnvelopeDispatcher listener,
        ILogger<OutboxRelay> logger,
        Action<IServiceCollection>? registerServices = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IOutboxStore>(_ => store);
        services.AddSingleton(listener);
        registerServices?.Invoke(services);
        var provider = services.BuildServiceProvider();

        return new OutboxRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            CreateDispatcher(),
            Options.Create(new OutboxOptions { MaxRetries = 3 }),
            logger);
    }

    private static void AssertScopeDisposeFailureLogged(SpyLoggerProvider spy, OutboxEntry entry)
    {
        var disposeFailed = spy.Entries.Should().ContainSingle(e => e.EventId.Id == 322).Which;
        disposeFailed.EventId.Name.Should().Be("OutboxScopeDisposeFailed");
        disposeFailed.Level.Should().Be(LogLevel.Warning);
        disposeFailed.Message.Should().Contain(entry.Id.ToString(CultureInfo.InvariantCulture)).And.Contain(entry.Type);
        disposeFailed.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Simulated scope disposal failure");
    }

    private static void AssertClaimLostLogged(
        IReadOnlyList<(LogLevel Level, EventId Id, string Message)> entries,
        OutboxEntry entry)
    {
        var claimLost = entries.Should().ContainSingle(e => e.Id.Id == 321).Which;
        claimLost.Id.Name.Should().Be("ClaimLost");
        claimLost.Level.Should().Be(LogLevel.Warning);
        claimLost.Message.Should().Contain(entry.Id.ToString(CultureInfo.InvariantCulture)).And.Contain(entry.Type);
    }

    [TestMethod]
    public async Task BackOffExponentiallyAfterAFailedSend()
    {
        // Arrange -- one failing entry per number of earlier failed attempts, under the default backoff:
        // 5 seconds after the first failure, doubling with each one after it, never more than 5 minutes
        int[] earlierFailures = [0, 1, 2, 3, 6, 9];
        var entries = earlierFailures
            .Select((retryCount, index) => MakeEntry(id: index + 1, type: "Orders.Events.OrderCreated", retryCount: retryCount))
            .ToList();
        var store = new CapturingStubOutboxStore(entries);
        var transport = new ThrowingTransport(new InvalidOperationException("broker down"));
        var relay = BuildRelay(store, CreateDispatcher(transport), new OutboxOptions { MaxRetries = 20 });

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        store.RetryDelays.Should().Equal(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task BackOffWithinTheConfiguredBounds()
    {
        // Arrange
        int[] earlierFailures = [0, 1, 2];
        var entries = earlierFailures
            .Select((retryCount, index) => MakeEntry(id: index + 1, type: "Orders.Events.OrderCreated", retryCount: retryCount))
            .ToList();
        var store = new CapturingStubOutboxStore(entries);
        var transport = new ThrowingTransport(new InvalidOperationException("broker down"));
        var relay = BuildRelay(
            store,
            CreateDispatcher(transport),
            new OutboxOptions
            {
                MaxRetries = 20,
                InitialBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(3),
            });

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        store.RetryDelays.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
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
    public async Task SendARoutedFireAndForgetCommandWithoutAwaitingAReply()
    {
        // Arrange -- a captured [FireAndForget] command routed to another service, classified by the relay's own scope
        const string type = "Billing.Commands.RecordCharge";
        var entry = MakeEntry(id: 5, type: type);
        var store = new CapturingStubOutboxStore([entry]);
        var billing = new SpyTransport();
        var services = new ServiceCollection();
        services.AddScoped<IOutboxStore>(_ => store);
        services.AddSingleton<ILocalEnvelopeDispatcher>(new FireAndForgetClassifier(type));
        services.AddKeyedSingleton<IConduitTransport>("billing", billing);
        var provider = services.BuildServiceProvider();
        var options = new ConduitOptions();
        options.Endpoints["billing"] = new ConduitEndpoint { Name = "billing" };
        options.Routes.Add(new ConduitRoute("Billing.Commands", "billing", Exact: false));
        var relay = new OutboxRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TransportDispatcher(options, provider, []),
            Options.Create(new OutboxOptions { MaxRetries = 3 }),
            NullLogger<OutboxRelay>.Instance);

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert -- sent with no reply awaited, so a transport's response timeout cannot fail a delivered command
        billing.SentEvents.Should().ContainSingle().Which.Type.Should().Be(type);
        billing.SentRequests.Should().BeEmpty();
        store.MarkedSent.Should().ContainSingle().Which.Should().Be(5L);
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

/// <summary>Records the <see cref="MessageContext"/> current when the relay releases a failed entry.</summary>
file sealed class ContextRecordingStubOutboxStore(IReadOnlyList<OutboxEntry> pending) : StubOutboxStore
{
    public MessageContext? ContextAtRetry { get; private set; }

    public override Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
        => Task.FromResult(pending);

    public override Task<bool> IncrementRetryAsync(
        long entryId,
        string claimToken,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        this.ContextAtRetry = MessageContext.Current;
        return base.IncrementRetryAsync(entryId, claimToken, retryDelay, cancellationToken);
    }
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

    public Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

file sealed class ThrowingTransport(Exception exception) : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;

    public Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken)
        => throw exception;
}

/// <summary>Local dispatcher stub whose local delivery always throws, to exercise the relay's retry path.</summary>
file sealed class ThrowingLocalDispatcher : ILocalEnvelopeDispatcher
{
    public bool DeliversLocally(string messageType) => true;

    public bool IsLocalOnly(string messageType) => false;

    public bool IsFireAndForget(string fullTypeName) => false;

    public bool IsEvent(string fullTypeName) => false;

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

/// <summary>
/// A local-only listener that attaches <paramref name="observer"/> to the context it is delivered under: the entry's
/// own context, so the relay's observations of that entry reach the observer.
/// </summary>
file sealed class ObserverAttachingListener(IConduitObserver observer) : ILocalEnvelopeDispatcher
{
    public bool DeliversLocally(string messageType) => true;

    public bool IsLocalOnly(string messageType) => true;

    public bool IsFireAndForget(string fullTypeName) => false;

    public bool IsEvent(string fullTypeName) => true;

    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        MessageContext.Current!.AttachObserver(observer);
        return Task.CompletedTask;
    }
}

/// <summary>An observer that throws on every observation.</summary>
file sealed class ThrowingObserver : IConduitObserver
{
    public void OnObservation(MessageObservation observation, MessageContext context)
        => throw new InvalidOperationException("Simulated observer failure");
}
