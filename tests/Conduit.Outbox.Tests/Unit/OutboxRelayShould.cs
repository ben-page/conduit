using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Outbox.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace Conduit.Outbox.Tests.Unit;

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

    private static OutboxRelay BuildRelay(
        IOutboxStore store,
        IEnumerable<IConduitTransport> transports,
        OutboxOptions? outboxOptions = null,
        IServiceProvider? serviceProvider = null)
    {
        var options = Options.Create(outboxOptions ?? new OutboxOptions { MaxRetries = 3 });
        var conduitOptions = new ConduitOptions();
        serviceProvider ??= new ServiceCollection().BuildServiceProvider();

        return new OutboxRelay(
            store,
            transports,
            serviceProvider,
            options,
            conduitOptions,
            NullLogger<OutboxRelay>.Instance);
    }

    [TestMethod]
    public async Task SendClaimedEntriesToTransport()
    {
        // Arrange
        var entry = MakeEntry(id: 1, type: "Orders.Events.OrderCreated");
        var store = new CapturingStubOutboxStore([entry]);
        var transport = new CapturingTransport();
        var relay = BuildRelay(store, [transport]);

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
        var relay = BuildRelay(store, [transport], new OutboxOptions { MaxRetries = 3 });

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
        var relay = BuildRelay(store, [transport], new OutboxOptions { MaxRetries = 3 });

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
        var relay = BuildRelay(store, [transport]);

        // Act
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        transport.SentEventEnvelopes.Should().BeEmpty();
        store.MarkedSent.Should().BeEmpty();
        store.RetriesIncremented.Should().BeEmpty();
        store.MarkedFailed.Should().BeEmpty();
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
}

file sealed class ThrowingTransport(Exception exception) : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw exception;
}
