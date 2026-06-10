using AwesomeAssertions;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// End-to-end integration test covering the full dead letter queue flow:
/// capture → browse → replay (via transport) → discard.
/// Uses <see cref="DbDeadLetterStore"/> with SQLite in-memory and a <see cref="SpyTransport"/>.
/// </summary>
[TestClass]
public sealed class DeadLetterEndToEndShould : IAsyncDisposable
{
    private StubConnectionFactory connectionFactory = null!;
    private SpyTransport transport = null!;
    private DbDeadLetterStore store = null!;
    private DeadLetters deadLetters = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        this.connectionFactory = new StubConnectionFactory();
        this.transport = new SpyTransport();

        this.store = new DbDeadLetterStore(this.connectionFactory, new SqliteDialect());
        await this.store.EnsureTableExistsAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConduitTransport>("orders", this.transport);
        var serviceProvider = services.BuildServiceProvider();

        this.deadLetters = new DeadLetters(
            this.store,
            serviceProvider,
            NullLogger<DeadLetters>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.connectionFactory.DisposeAsync();
    }

    [TestMethod]
    public async Task CaptureAndReplayMessage()
    {
        // Arrange — simulate capture (normally done by BrokerTransportBase)
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            MessageType = "Orders.Commands.CreateOrder",
            Body = new BinaryData("""{"orderId":"abc-123"}"""u8.ToArray()),
            SourceEndpoint = "orders",
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "Database connection failed",
            MessageId = Guid.NewGuid().ToString("N"),
            CausationId = null,
            ConversationId = Guid.NewGuid().ToString("N"),
            AttemptCount = 1,
            DeadLetteredAt = DateTimeOffset.UtcNow,
        };

        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act — browse
        var page = await this.deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);

        // Assert
        page.Entries.Should().HaveCount(1);
        page.Entries[0].MessageType.Should().Be("Orders.Commands.CreateOrder");
        page.Entries[0].ExceptionMessage.Should().Be("Database connection failed");

        // Act — get by ID
        var retrieved = await this.deadLetters.GetAsync(entry.Id, CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(entry.Id);

        // Act — replay
        var result = await this.deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        this.transport.Redelivered.Should().HaveCount(1);
        this.transport.Redelivered[0].Type.Should().Be("Orders.Commands.CreateOrder");

        // Verify entry removed from store
        var afterReplay = await this.deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
        afterReplay.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CaptureAndDiscardMessage()
    {
        // Arrange
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            MessageType = "Orders.Commands.CreateOrder",
            Body = new BinaryData("""{"orderId":"def-456"}"""u8.ToArray()),
            SourceEndpoint = "orders",
            ExceptionType = "System.ArgumentException",
            ExceptionMessage = "Invalid order data",
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = Guid.NewGuid().ToString("N"),
            AttemptCount = 1,
            DeadLetteredAt = DateTimeOffset.UtcNow,
        };

        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act
        await this.deadLetters.DiscardAsync(entry.Id, CancellationToken.None);

        // Assert
        var afterDiscard = await this.deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
        afterDiscard.Entries.Should().BeEmpty();
        this.transport.Redelivered.Should().BeEmpty("discard should not replay");
    }

    [TestMethod]
    public async Task CaptureMultipleAndReplayByType()
    {
        // Arrange
        for (var i = 0; i < 3; i++)
        {
            await this.store.WriteAsync(new DeadLetterEntry
            {
                Id = Guid.NewGuid(),
                MessageType = "Orders.Commands.CreateOrder",
                Body = new BinaryData("""{"orderId":"test"}"""u8.ToArray()),
                SourceEndpoint = "orders",
                ExceptionType = "System.Exception",
                ExceptionMessage = $"Failure {i}",
                MessageId = Guid.NewGuid().ToString("N"),
                ConversationId = Guid.NewGuid().ToString("N"),
                AttemptCount = 1,
                DeadLetteredAt = DateTimeOffset.UtcNow.AddMilliseconds(-i * 10),
            }, CancellationToken.None);
        }

        await this.store.WriteAsync(new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            MessageType = "Orders.Commands.ShipOrder",
            Body = new BinaryData("""{"orderId":"ship-1"}"""u8.ToArray()),
            SourceEndpoint = "orders",
            ExceptionType = "System.Exception",
            ExceptionMessage = "Different type",
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = Guid.NewGuid().ToString("N"),
            AttemptCount = 1,
            DeadLetteredAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Act
        var results = await this.deadLetters.ReplayByTypeAsync("Orders.Commands.CreateOrder", CancellationToken.None);

        // Assert
        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.Success);
        this.transport.Redelivered.Should().HaveCount(3);

        var remaining = await this.deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
        remaining.Entries.Should().HaveCount(1);
        remaining.Entries[0].MessageType.Should().Be("Orders.Commands.ShipOrder");
    }
}
