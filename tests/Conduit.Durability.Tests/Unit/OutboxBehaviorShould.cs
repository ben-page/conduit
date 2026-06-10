using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Persistence;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class OutboxBehaviorShould
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
    public async Task WriteCapturedMessagesToStore()
    {
        // Arrange
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        capture.Intercept(new DispatchEnvelope("Orders.OrderCreated", JsonSerializer.SerializeToElement(new { OrderId = 1 })));

        var behavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        var result = await behavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);

        // Assert
        result.Should().Be("response");
        store.SavedEntries.Should().HaveCount(1);
        store.SavedEntries[0].Type.Should().Be("Orders.OrderCreated");
        store.SavedEntries[0].Payload.Should().Be("""{"OrderId":1}""");
        store.SavedEntries[0].MessageId.Should().NotBeNullOrEmpty();
        store.SavedEntries[0].ConversationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task SkipPersistenceWhenNoTransaction()
    {
        // Arrange
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: false);

        capture.Intercept(new DispatchEnvelope("Orders.OrderCreated", JsonSerializer.SerializeToElement(new { })));

        var behavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        var result = await behavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);

        // Assert
        result.Should().Be("response");
        store.SavedEntries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task PassThroughWhenNothingCaptured()
    {
        // Arrange
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        var behavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        var result = await behavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);

        // Assert
        result.Should().Be("response");
        store.SavedEntries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GiveEachCapturedMessageItsOwnMessageIdCausedByTheTriggeringMessage()
    {
        // Arrange — a handler emits two outbound messages within one dispatch.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        capture.Intercept(new DispatchEnvelope("Orders.OrderCreated", JsonSerializer.SerializeToElement(new { OrderId = 1 })));
        capture.Intercept(new DispatchEnvelope("Orders.OrderShipped", JsonSerializer.SerializeToElement(new { OrderId = 1 })));

        var behavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Establish the triggering message's context (the message currently being handled).
        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("trigger-msg-id", null, "conv-99");
        try
        {
            // Act
            await behavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }

        // Assert — two entries, each with its OWN distinct MessageId...
        store.SavedEntries.Should().HaveCount(2);
        store.SavedEntries.Select(e => e.MessageId).Distinct().Should().HaveCount(2);
        store.SavedEntries.Should().AllSatisfy(e => e.MessageId.Should().NotBe("trigger-msg-id"));

        // ...and the triggering message recorded as the cause of each, with conversation preserved.
        store.SavedEntries.Should().AllSatisfy(e =>
        {
            e.CausationId.Should().Be("trigger-msg-id");
            e.ConversationId.Should().Be("conv-99");
        });
    }

    [TestMethod]
    public async Task SetWrittenMessagesOnCaptureAfterSave()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var capture = new OutboxCapture(store, CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance);
        var factory = new StubTransactionFactory(hasTransaction: true);

        var envelope = new DispatchEnvelope("Test.OrderPlaced", JsonSerializer.SerializeToElement(new { id = 1 }));
        capture.Intercept(envelope);

        var behavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        await behavior.HandleAsync("request", () => Task.FromResult("result"), CancellationToken.None);

        // Assert -- after behavior runs, FlushAsync should be able to send
        var savedMessageId = store.SavedEntries[0].MessageId;
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 1, MessageId = savedMessageId }];

        await capture.FlushAsync(CancellationToken.None);

        transport.SentEvents.Should().HaveCount(1);
        transport.SentEvents[0].Type.Should().Be("Test.OrderPlaced");
        store.MarkedSent.Should().BeEquivalentTo([1L]);
    }
}

file sealed class StubTransactionFactory(bool hasTransaction) : IConduitConnectionFactory
{
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public DbTransaction? GetCurrentTransaction()
        => hasTransaction ? new StubTransaction() : null;
}

file sealed class StubTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => null;
    public override void Commit() { }
    public override void Rollback() { }
}
