using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// <see cref="DbAuditStore.SaveBatchAsync"/> writes a batch on one connection in one transaction: a failure part-way
/// persists none of it, so the publisher's "batch dropped" log is the truth.
/// </summary>
[TestClass]
public sealed class DbAuditStoreShould
{
    [TestMethod]
    public async Task WriteABatchAtomically()
    {
        // Arrange — five records, the third of which cannot be written
        await using var factory = new CountingConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        var records = Enumerable.Range(1, 5)
            .Select(index => NewRecord($"atomic-{index}", messageType: index == 3 ? null! : "Test.Commands.DoThing"))
            .ToList();
        var connectionsBefore = factory.CreatedCount;

        // Act
        var save = () => store.SaveBatchAsync(records, CancellationToken.None);

        // Assert — the batch fails whole: the two records written before the failure are rolled back
        await save.Should().ThrowAsync<InvalidOperationException>();
        (factory.CreatedCount - connectionsBefore).Should().Be(1, "a batch is written on one connection");
        foreach (var record in records)
        {
            (await store.GetByMessageIdAsync(record.MessageId, CancellationToken.None)).Should().BeNull();
        }
    }

    [TestMethod]
    public async Task ReturnTheLatestRecordOfAMessageId()
    {
        // Arrange — a dispatch silent past the stale timeout stored a provisional record, then its final one
        await using var factory = new CountingConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        var marker = new CustomObservation("Conduit.Audit.StaleGroupEviction", null);
        var invoked = new DispatchInvoked("Test.Jobs.Reindex", "Command");
        await store.SaveBatchAsync([NewRecord("silent-1", "Test.Jobs.Reindex", [invoked, marker])], CancellationToken.None);
        await store.SaveBatchAsync(
            [NewRecord("silent-1", "Test.Jobs.Reindex", [invoked, marker, new DispatchCompleted(360_000)])],
            CancellationToken.None);

        // Act
        var read = await store.GetByMessageIdAsync("silent-1", CancellationToken.None);

        // Assert — the final record, not the provisional one
        read!.Observations[^1].Should().BeOfType<DispatchCompleted>();
    }

    [TestMethod]
    public async Task ReturnTheRealRecordEvenWhenItsProvisionalOneWasStoredLater()
    {
        // Arrange — a collector persisting batches concurrently stored the real record first
        await using var factory = new CountingConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);
        var marker = new CustomObservation("Conduit.Audit.StaleGroupEviction", null);
        var invoked = new DispatchInvoked("Test.Jobs.Reindex", "Command");
        await store.SaveBatchAsync(
            [NewRecord("silent-2", "Test.Jobs.Reindex", [invoked, marker, new DispatchCompleted(360_000)])],
            CancellationToken.None);
        await store.SaveBatchAsync([NewRecord("silent-2", "Test.Jobs.Reindex", [invoked, marker])], CancellationToken.None);

        // Act
        var read = await store.GetByMessageIdAsync("silent-2", CancellationToken.None);

        // Assert — the real record, though the provisional one has the higher Id
        read!.Observations[^1].Should().BeOfType<DispatchCompleted>();
    }

    private static DispatchAuditRecord NewRecord(string messageId, string messageType)
        => NewRecord(messageId, messageType, [new DispatchInvoked(messageType, "Command"), new DispatchCompleted(1.0)]);

    private static DispatchAuditRecord NewRecord(string messageId, string messageType, IReadOnlyList<MessageObservation> observations)
    {
        return new DispatchAuditRecord
        {
            MessageId = messageId,
            ConversationId = "conv-1",
            MessageType = messageType,
            MessageKind = "Command",
            ServiceName = "TestService",
            Timestamp = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Observations = observations,
        };
    }
}
