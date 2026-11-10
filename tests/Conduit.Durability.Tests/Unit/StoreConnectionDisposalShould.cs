using AwesomeAssertions;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Tests.Support;
using Conduit.TestUtils;

namespace Conduit.Durability.Tests.Unit;

/// <summary>
/// Verifies that every store method disposes the connection it obtains from the
/// connection factory. A leaked connection would leave CreatedCount > DisposedCount.
/// </summary>
[TestClass]
public sealed class StoreConnectionDisposalShould
{
    [TestMethod]
    public async Task DisposeEveryConnectionTheOutboxStoreOpens()
    {
        // Arrange
        await using var factory = new CountingConnectionFactory();
        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());

        // Act — exercise every method that opens a connection via the factory.
        await store.EnsureTableExistsAsync(CancellationToken.None);
        await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await store.ClaimByMessageIdsAsync(["msg-1"], CancellationToken.None);
        await store.MarkSentAsync(1, "claim-token", CancellationToken.None);
        await store.IncrementRetryAsync(1, "claim-token", TimeSpan.Zero, CancellationToken.None);
        await store.MarkFailedAsync(1, "claim-token", CancellationToken.None);
        await store.DeleteCompletedAsync(TimeSpan.Zero, CancellationToken.None);

        // Assert — every opened connection was disposed.
        factory.CreatedCount.Should().BeGreaterThan(0);
        factory.DisposedCount.Should().Be(factory.CreatedCount);
    }

    [TestMethod]
    public async Task DisposeEveryConnectionTheInboxStoreOpens()
    {
        // Arrange
        await using var factory = new CountingConnectionFactory();
        var store = new DbConnectionInboxStore(factory, new SqliteDialect());

        // Act
        await store.EnsureTableExistsAsync(CancellationToken.None);
        await store.RecordAsync("msg-1", CancellationToken.None);
        await store.ExistsAsync("msg-1", CancellationToken.None);
        await store.RemoveAsync("msg-1", CancellationToken.None);
        await store.DeleteExpiredAsync(TimeSpan.Zero, CancellationToken.None);

        // Assert
        factory.CreatedCount.Should().BeGreaterThan(0);
        factory.DisposedCount.Should().Be(factory.CreatedCount);
    }

    [TestMethod]
    public async Task DisposeEveryConnectionTheDeadLetterStoreOpens()
    {
        // Arrange
        await using var factory = new CountingConnectionFactory();
        var store = new DbDeadLetterStore(factory, new SqliteDialect());

        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            MessageType = "Some.Message",
            Body = BinaryData.FromString("{}"),
            SourceEndpoint = "endpoint",
            AttemptCount = 1,
            DeadLetteredAt = DateTimeOffset.UtcNow,
        };

        // Act
        await store.EnsureTableExistsAsync(CancellationToken.None);
        await store.WriteAsync(entry, CancellationToken.None);
        await store.GetAsync(entry.Id, CancellationToken.None);
        await store.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
        await store.RemoveAsync(entry.Id, CancellationToken.None);
        await store.RemoveAsync([Guid.NewGuid()], CancellationToken.None);

        // Assert
        factory.CreatedCount.Should().BeGreaterThan(0);
        factory.DisposedCount.Should().Be(factory.CreatedCount);
    }
}
