using AwesomeAssertions;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.TestUtils;

namespace Conduit.Durability.Tests.Unit;

/// <summary>
/// The outbox claim must be atomic: a pending row can be claimed by exactly one worker.
/// Two concurrent claimers of the same pending rows must receive DISJOINT sets, otherwise
/// the relay and the immediate-send flush would both dispatch the same message (double-send).
/// </summary>
[TestClass]
public sealed class OutboxClaimAtomicityShould
{
    [TestMethod]
    public async Task GiveConcurrentClaimersOfPendingEntriesDisjointSets()
    {
        // Arrange — a file-backed SQLite db so two claimers run on independent connections.
        await using var factory = new CountingConnectionFactory();
        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        const int entryCount = 30;
        await SeedPendingEntriesAsync(factory, entryCount);

        // Act — two claimers race for the same pending rows.
        var claimerA = store.ClaimPendingAsync(batchSize: entryCount, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var claimerB = store.ClaimPendingAsync(batchSize: entryCount, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var results = await Task.WhenAll(claimerA, claimerB);

        // Assert — no row is claimed by both, and together they claim each row at most once.
        var idsFromA = results[0].Select(e => e.Id).ToList();
        var idsFromB = results[1].Select(e => e.Id).ToList();

        idsFromA.Should().NotIntersectWith(idsFromB);
        var allClaimed = idsFromA.Concat(idsFromB).ToList();
        allClaimed.Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public async Task GiveConcurrentClaimersByMessageIdDisjointSets()
    {
        // Arrange
        await using var factory = new CountingConnectionFactory();
        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var messageIds = Enumerable.Range(0, 30).Select(i => $"msg-{i}").ToList();
        await SeedPendingEntriesAsync(factory, messageIds);

        // Act — relay-style claim and flush-style claim race for the same rows.
        var byIds = store.ClaimByMessageIdsAsync(messageIds, CancellationToken.None);
        var byPending = store.ClaimPendingAsync(batchSize: 30, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await Task.WhenAll(byIds, byPending);

        var idsFromFlush = (await byIds).Select(e => e.Id).ToList();
        var idsFromRelay = (await byPending).Select(e => e.Id).ToList();

        // Assert — disjoint: no row dispatched by both paths.
        idsFromFlush.Should().NotIntersectWith(idsFromRelay);
    }

    private static Task SeedPendingEntriesAsync(CountingConnectionFactory factory, int count)
        => SeedPendingEntriesAsync(factory, Enumerable.Range(0, count).Select(i => $"msg-{i}").ToList());

    private static async Task SeedPendingEntriesAsync(CountingConnectionFactory factory, IReadOnlyList<string> messageIds)
    {
        await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());
        var entries = messageIds.Select(id => new OutboxEntry
        {
            MessageId = id,
            ConversationId = "conv-1",
            Type = "Test.Event",
            Payload = "{}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        }).ToList();

        // SaveAsync writes via the supplied transaction's own connection, so it does not matter
        // that the store's factory would otherwise open a separate connection.
        await store.SaveAsync(entries, transaction, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }
}
