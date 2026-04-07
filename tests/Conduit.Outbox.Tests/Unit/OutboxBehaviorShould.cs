using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Outbox.Runtime;
using Conduit.Outbox.Store;
using Conduit.Outbox.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Conduit.Outbox.Tests.Unit;

[TestClass]
public class OutboxBehaviorShould
{
    private static OutboxCapture CreateCapture(StubOutboxStore? store = null, IConduitTransport[]? transports = null)
    {
        return new OutboxCapture(
            store ?? new StubOutboxStore(),
            transports ?? [],
            new ServiceCollection().BuildServiceProvider(),
            new ConduitOptions());
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
}

file sealed class StubTransactionFactory(bool hasTransaction) : IOutboxConnectionFactory
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
