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

    private static DispatchAuditRecord NewRecord(string messageId, string messageType) => new()
    {
        MessageId = messageId,
        ConversationId = "conv-1",
        MessageType = messageType,
        MessageKind = "Command",
        ServiceName = "TestService",
        Timestamp = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Observations = [new DispatchInvoked(messageType, "Command"), new DispatchCompleted(1.0)],
    };
}
