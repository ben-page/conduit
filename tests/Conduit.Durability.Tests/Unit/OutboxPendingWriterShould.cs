using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Persistence;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class OutboxPendingWriterShould
{
    private static OutboxCapture CreateCapture()
    {
        return new OutboxCapture(
            new TransportDispatcher(new ConduitOptions(), new ServiceCollection().BuildServiceProvider(), []),
            new TransactionFactory(hasTransaction: true),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConduitBackgroundDispatcher(NullLogger<ConduitBackgroundDispatcher>.Instance),
            NullLogger<OutboxCapture>.Instance);
    }

    private static DispatchEnvelope Envelope(string type)
        => new(type, JsonSerializer.SerializeToElement(new { }));

    [TestMethod]
    public async Task WriteEnvelopesCapturedAfterThePipelineExited()
    {
        // Arrange — the pipeline's OutboxBehavior writes the first envelope; a host's outermost
        // behavior then captures a second one (e.g. while dispatching domain events) after every
        // OutboxBehavior in the scope has already exited.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new TransactionFactory(hasTransaction: true);
        var behavior = new OutboxBehavior<string, string>(capture, store, factory);
        var writer = new OutboxPendingWriter(capture, store, factory);

        capture.Intercept(Envelope("Test.InPipeline"));
        await behavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);
        capture.Intercept(Envelope("Test.AfterPipeline"));

        // Act
        await writer.WritePendingAsync(CancellationToken.None);

        // Assert — each envelope written once, and both are in the list the flush delivers.
        store.SavedEntries.Select(e => e.Type).Should().Equal("Test.InPipeline", "Test.AfterPipeline");
        capture.WrittenMessages.Select(m => m.MessageId).Should().Equal(store.SavedEntries.Select(e => e.MessageId));
    }

    [TestMethod]
    public async Task StampEntriesLikeTheBehaviorDoes()
    {
        // Arrange
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var writer = new OutboxPendingWriter(capture, store, new TransactionFactory(hasTransaction: true));

        capture.Intercept(Envelope("Test.Notice"));

        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("trigger-msg-id", null, "conv-7");
        try
        {
            // Act
            await writer.WritePendingAsync(CancellationToken.None);
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }

        // Assert
        var entry = store.SavedEntries.Should().ContainSingle().Subject;
        entry.MessageId.Should().NotBeNullOrEmpty().And.NotBe("trigger-msg-id");
        entry.CausationId.Should().Be("trigger-msg-id");
        entry.ConversationId.Should().Be("conv-7");
        entry.Payload.Should().Be("{}");
    }

    [TestMethod]
    public async Task StampEntriesWithTheInjectedClock()
    {
        // Arrange — the behavior writes one envelope and the writer the one captured after it, both on a clock years
        // away from the machine's
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new TransactionFactory(hasTransaction: true);
        var behavior = new OutboxBehavior<string, string>(capture, store, factory, options: null, clock);
        var writer = new OutboxPendingWriter(capture, store, factory, options: null, clock);

        capture.Intercept(Envelope("Test.InPipeline"));
        await behavior.HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);
        capture.Intercept(Envelope("Test.AfterPipeline"));

        // Act
        await writer.WritePendingAsync(CancellationToken.None);

        // Assert
        store.SavedEntries.Select(entry => entry.CreatedAt).Should().Equal(now.UtcDateTime, now.UtcDateTime);
    }

    [TestMethod]
    public async Task WriteNothingWhenEverythingIsWritten()
    {
        // Arrange
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var factory = new TransactionFactory(hasTransaction: true);
        var writer = new OutboxPendingWriter(capture, store, factory);

        capture.Intercept(Envelope("Test.Notice"));
        await new OutboxBehavior<string, string>(capture, store, factory)
            .HandleAsync("request", () => Task.FromResult("response"), CancellationToken.None);

        // Act
        await writer.WritePendingAsync(CancellationToken.None);
        await writer.WritePendingAsync(CancellationToken.None);

        // Assert
        store.SavedEntries.Should().ContainSingle();
        capture.WrittenMessages.Should().ContainSingle();
    }

    [TestMethod]
    public async Task WriteNothingWithoutATransaction()
    {
        // Arrange — capture saw a transaction; the writer's factory reports none at write time.
        var capture = CreateCapture();
        var store = new StubOutboxStore();
        var writer = new OutboxPendingWriter(capture, store, new TransactionFactory(hasTransaction: false));

        capture.Intercept(Envelope("Test.Notice"));

        // Act
        await writer.WritePendingAsync(CancellationToken.None);

        // Assert — nothing saved, and the envelope is still pending for a write that has one.
        store.SavedEntries.Should().BeEmpty();
        capture.WrittenMessages.Should().BeEmpty();
        capture.Unwritten.Should().ContainSingle();
    }
}

file sealed class TransactionFactory(bool hasTransaction) : IConduitConnectionFactory
{
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public DbTransaction? GetCurrentTransaction()
        => hasTransaction ? new FakeTransaction() : null;
}

file sealed class FakeTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection? DbConnection => null;
    public override void Commit() { }
    public override void Rollback() { }
}
