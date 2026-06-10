using AwesomeAssertions;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Runtime.Inbox;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Durability.Tests.Unit;

/// <summary>
/// Exercises the inbox dedup gate under genuine concurrency. Two redeliveries of the same
/// MessageId arriving at once must result in exactly one "proceed" decision — the atomic
/// insert, not a check-then-act, is what guarantees this.
/// </summary>
[TestClass]
public sealed class InboxConcurrentDedupShould
{
    [TestMethod]
    public async Task AllowExactlyOneOfManyConcurrentRedeliveries()
    {
        // Arrange — a shared file-backed SQLite store so concurrent connections contend
        // for the same row (an in-memory db would serialize behind one connection).
        await using var factory = new CountingConnectionFactory();
        var store = new DbConnectionInboxStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        const string messageId = "redelivered-001";
        const int concurrency = 16;

        // Each task simulates an inbound redelivery with the same MessageId. We capture the
        // gate's decision per task while flowing a per-task MessageContext.
        async Task<bool> RunGateAsync()
        {
            var interceptor = new InboxInterceptor(store);
            var previous = MessageContext.Current;
            MessageContext.StartDispatchFromTransport(messageId, null, "conv-001");
            try
            {
                return await interceptor.ShouldProcessAsync("Some.Command", CancellationToken.None);
            }
            finally
            {
                MessageContext.RestoreContext(previous);
            }
        }

        // Act — fire all redeliveries concurrently.
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(RunGateAsync)).ToArray();
        var results = await Task.WhenAll(tasks);

        // Assert — exactly one redelivery is allowed to proceed; the rest are discarded.
        results.Count(allowed => allowed).Should().Be(1);
    }
}
