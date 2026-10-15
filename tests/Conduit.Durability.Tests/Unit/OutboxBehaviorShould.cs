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

    private static IServiceScopeFactory CreateEmptyScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    // ClaimAndDeliverAsync resolves IOutboxStore from its own fresh scope rather than taking it as
    // a parameter — this wires the given stub into a real scope factory so it can be resolved.
    private static IServiceScopeFactory CreateScopeFactory(IOutboxStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // A real (but never-started) dispatcher: none of these tests need the background dispatcher to
    // actually drain — FlushAsync is only exercised for its enqueue-vs-not behavior, and the one
    // test that needs the delivery routine's actual effects invokes it directly.
    private static ConduitBackgroundDispatcher CreateBackgroundDispatcher()
        => new(NullLogger<ConduitBackgroundDispatcher>.Instance);

    // Capture is given a transaction-present factory so Intercept accepts the envelopes under test:
    // these tests exercise OutboxBehavior's own logic, using capture only to pre-load captured state.
    private static OutboxCapture CreateCapture(TransportDispatcher? dispatcher = null)
    {
        return new OutboxCapture(
            dispatcher ?? CreateDispatcher(),
            new StubTransactionFactory(hasTransaction: true),
            CreateEmptyScopeFactory(),
            CreateBackgroundDispatcher(),
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
        // Arrange — capture pre-loads an envelope (it saw a transaction at capture time), but the
        // behavior's own connection factory reports none, so OutboxBehavior must skip persistence.
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
    public async Task AddWrittenMessagesOnCaptureAfterSave()
    {
        // Arrange
        var store = new StubOutboxStore();
        var transport = new SpyTransport();
        var factory = new StubTransactionFactory(hasTransaction: true);
        var capture = new OutboxCapture(CreateDispatcher(transport), factory, CreateEmptyScopeFactory(), CreateBackgroundDispatcher(), NullLogger<OutboxCapture>.Instance);

        var envelope = new DispatchEnvelope("Test.OrderPlaced", JsonSerializer.SerializeToElement(new { id = 1 }));
        capture.Intercept(envelope);

        var behavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        await behavior.HandleAsync("request", () => Task.FromResult("result"), CancellationToken.None);

        // Assert -- after behavior runs, capture's correlated messages should be deliverable.
        // Exercises the delivery routine directly (rather than capture.FlushAsync, which now only
        // schedules it on the background dispatcher) against the exact messages OutboxBehavior
        // correlated via AddWrittenMessages, proving the MessageId round-trips correctly.
        var savedMessageId = store.SavedEntries[0].MessageId;
        store.ClaimedEntries = [new ClaimedOutboxEntry { Id = 1, MessageId = savedMessageId }];

        await OutboxCapture.ClaimAndDeliverAsync(
            CreateScopeFactory(store), CreateDispatcher(transport), NullLogger<OutboxCapture>.Instance, capture.WrittenMessages, CancellationToken.None);

        transport.SentEvents.Should().HaveCount(1);
        transport.SentEvents[0].Type.Should().Be("Test.OrderPlaced");
        store.MarkedSent.Should().BeEquivalentTo([1L]);
    }

    [TestMethod]
    public async Task WriteEachCapturedEnvelopeOnceAcrossNestedPipelines()
    {
        // Arrange — one dispatch scope (one capture): the command handler captures one envelope,
        // then publishes two events inline, whose listener pipelines each run their own
        // OutboxBehavior inside the command's pipeline before the command's own behavior runs.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        var commandBehavior = new OutboxBehavior<string, string>(capture, store, factory);
        var firstListenerBehavior = new OutboxBehavior<string, string>(capture, store, factory);
        var secondListenerBehavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        await commandBehavior.HandleAsync(
            "command",
            async () =>
            {
                capture.Intercept(new DispatchEnvelope("Test.UrgentNotice", JsonSerializer.SerializeToElement(new { Id = 1 })));
                await firstListenerBehavior.HandleAsync("first", () => Task.FromResult("first"), CancellationToken.None);
                await secondListenerBehavior.HandleAsync("second", () => Task.FromResult("second"), CancellationToken.None);
                return "command";
            },
            CancellationToken.None);

        // Assert — one row, and that row is the one the post-commit flush delivers.
        store.SavedEntries.Should().ContainSingle();
        capture.WrittenMessages.Should().ContainSingle();
        capture.WrittenMessages[0].MessageId.Should().Be(store.SavedEntries[0].MessageId);
    }

    [TestMethod]
    public async Task AccumulateWrittenMessagesAcrossPipelinesInOneScope()
    {
        // Arrange — the inner pipeline writes the first envelope; the outer handler then captures
        // a second one after the inner pipeline has returned.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        var outerBehavior = new OutboxBehavior<string, string>(capture, store, factory);
        var innerBehavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        await outerBehavior.HandleAsync(
            "outer",
            async () =>
            {
                capture.Intercept(new DispatchEnvelope("Test.First", JsonSerializer.SerializeToElement(new { })));
                await innerBehavior.HandleAsync("inner", () => Task.FromResult("inner"), CancellationToken.None);
                capture.Intercept(new DispatchEnvelope("Test.Second", JsonSerializer.SerializeToElement(new { })));
                return "outer";
            },
            CancellationToken.None);

        // Assert — each envelope written once, and BOTH are in the list the flush delivers (the
        // inner pipeline's written message is not replaced by the outer one's).
        store.SavedEntries.Select(e => e.Type).Should().Equal("Test.First", "Test.Second");
        capture.WrittenMessages.Select(m => m.MessageId).Should().Equal(store.SavedEntries.Select(e => e.MessageId));
        capture.WrittenMessages.Select(m => m.Envelope.Type).Should().Equal("Test.First", "Test.Second");
    }

    [TestMethod]
    public async Task LeaveEnvelopesUnwrittenWhenTheSaveFails()
    {
        // Arrange — the first save throws, so nothing reached the transaction. The envelope was
        // captured before the failing pipeline began (by an enclosing handler, not by the failed
        // unit), so a later pipeline in the same scope must still write it.
        var capture = CreateCapture();
        var store = new FailOnceOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        capture.Intercept(new DispatchEnvelope("Test.Notice", JsonSerializer.SerializeToElement(new { })));

        var failingBehavior = new OutboxBehavior<string, string>(capture, store, factory);
        var laterBehavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        var failing = () => failingBehavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);
        await failing.Should().ThrowAsync<InvalidOperationException>();
        await laterBehavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);

        // Assert
        store.SavedEntries.Should().ContainSingle(e => e.Type == "Test.Notice");
        capture.WrittenMessages.Should().ContainSingle();
    }

    [TestMethod]
    public async Task DiscardEnvelopesCapturedByAFaultedPipeline()
    {
        // Arrange — a command captures an envelope and then throws; a later command in the same DI
        // scope must not write the failed command's envelope into its own transaction.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        var failedBehavior = new OutboxBehavior<string, string>(capture, store, factory);
        var laterBehavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        var failed = () => failedBehavior.HandleAsync(
            "failed",
            () =>
            {
                capture.Intercept(new DispatchEnvelope("Test.FromFailedCommand", JsonSerializer.SerializeToElement(new { })));
                throw new InvalidOperationException("Simulated handler failure.");
            },
            CancellationToken.None);
        await failed.Should().ThrowAsync<InvalidOperationException>();

        await laterBehavior.HandleAsync(
            "later",
            () =>
            {
                capture.Intercept(new DispatchEnvelope("Test.FromLaterCommand", JsonSerializer.SerializeToElement(new { })));
                return Task.FromResult("later");
            },
            CancellationToken.None);

        // Assert
        store.SavedEntries.Select(e => e.Type).Should().Equal("Test.FromLaterCommand");
        capture.WrittenMessages.Select(m => m.Envelope.Type).Should().Equal("Test.FromLaterCommand");
    }

    [TestMethod]
    public async Task KeepTheEnclosingHandlersEnvelopesWhenANestedPipelineFaults()
    {
        // Arrange — the outer handler captures an envelope, then a nested pipeline captures its own
        // and throws; the outer handler catches and carries on. Only the nested pipeline's envelope
        // belongs to the failed unit.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new StubTransactionFactory(hasTransaction: true);

        var outerBehavior = new OutboxBehavior<string, string>(capture, store, factory);
        var innerBehavior = new OutboxBehavior<string, string>(capture, store, factory);

        // Act
        await outerBehavior.HandleAsync(
            "outer",
            async () =>
            {
                capture.Intercept(new DispatchEnvelope("Test.FromOuter", JsonSerializer.SerializeToElement(new { })));
                try
                {
                    await innerBehavior.HandleAsync(
                        "inner",
                        () =>
                        {
                            capture.Intercept(new DispatchEnvelope("Test.FromFailedInner", JsonSerializer.SerializeToElement(new { })));
                            throw new InvalidOperationException("Simulated listener failure.");
                        },
                        CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The outer handler tolerates the nested failure.
                }

                return "outer";
            },
            CancellationToken.None);

        // Assert
        store.SavedEntries.Select(e => e.Type).Should().Equal("Test.FromOuter");
    }
}

file sealed class FailOnceOutboxStore : StubOutboxStore
{
    private bool failed;

    public override Task SaveAsync(IReadOnlyList<OutboxEntry> entries, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (!this.failed)
        {
            this.failed = true;
            throw new InvalidOperationException("Simulated outbox write failure.");
        }

        return base.SaveAsync(entries, transaction, cancellationToken);
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
