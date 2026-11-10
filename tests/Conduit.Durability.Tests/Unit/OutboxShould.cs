using System.Globalization;
using AwesomeAssertions;
using Conduit.Durability.Outbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Generated;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Unit;

/// <summary>
/// The user-facing outbox surface over a real SQLite store: finding the entries whose retries ran out, and
/// handing them back to the relay. The same operations run on Postgres, SQL Server and MySQL in
/// <c>PersistenceEngineTestBase.RetryAFailedEntry</c>.
/// </summary>
[TestClass]
public sealed class OutboxShould : IAsyncDisposable
{
    private const string OrderPlaced = "Orders.Events.OrderPlaced";
    private const string OrderShipped = "Orders.Events.OrderShipped";

    private StubConnectionFactory connectionFactory = null!;
    private DbConnectionOutboxStore store = null!;
    private OutboxOperations outbox = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        this.connectionFactory = new StubConnectionFactory();
        this.store = new DbConnectionOutboxStore(this.connectionFactory, new SqliteDialect());
        await this.store.EnsureTableExistsAsync(CancellationToken.None);
        this.outbox = new OutboxOperations(this.store);
    }

    public async ValueTask DisposeAsync()
    {
        await this.connectionFactory.DisposeAsync();
    }

    [TestMethod]
    public async Task RetryAFailedEntry()
    {
        // Arrange — an entry whose retries ran out, its next attempt an hour out
        var failedId = await this.SaveFailedEntryAsync("msg-failed", OrderPlaced);

        // Act
        var retried = await this.outbox.RetryFailedAsync(failedId, CancellationToken.None);

        // Assert — pending again, with its whole retry budget back, and due at once
        retried.Should().BeTrue();
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var entry = claimed.Should().ContainSingle().Which;
        entry.Id.Should().Be(failedId);
        entry.RetryCount.Should().Be(0);
    }

    [TestMethod]
    public async Task RetryTheFailedEntriesOfAType()
    {
        // Arrange
        var placedIds = new[]
        {
            await this.SaveFailedEntryAsync("msg-placed-1", OrderPlaced),
            await this.SaveFailedEntryAsync("msg-placed-2", OrderPlaced),
        };
        var shippedId = await this.SaveFailedEntryAsync("msg-shipped", OrderShipped);

        // Act
        var retried = await this.outbox.RetryFailedAsync(OrderPlaced, CancellationToken.None);

        // Assert — only that type's entries go back to the relay
        retried.Should().Be(2);
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        claimed.Select(e => e.Id).Should().BeEquivalentTo(placedIds);
        claimed.Should().AllSatisfy(e => e.RetryCount.Should().Be(0));
        var stillFailed = await this.outbox.QueryFailedAsync(new FailedOutboxFilter(), CancellationToken.None);
        stillFailed.Entries.Should().ContainSingle().Which.Id.Should().Be(shippedId);
    }

    [TestMethod]
    public async Task NotRetryAnEntryThatHasNotFailed()
    {
        // Arrange — a sent entry must never be sent again by a retry
        await this.SaveEntryAsync("msg-sent", OrderPlaced);
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var sentId = claimed.Single().Id;
        await this.store.MarkSentAsync(sentId, claimed.Single().ClaimToken!, CancellationToken.None);

        // Act
        var retriedSent = await this.outbox.RetryFailedAsync(sentId, CancellationToken.None);
        var retriedMissing = await this.outbox.RetryFailedAsync(sentId + 1000, CancellationToken.None);
        var retriedByType = await this.outbox.RetryFailedAsync(OrderPlaced, CancellationToken.None);

        // Assert
        retriedSent.Should().BeFalse();
        retriedMissing.Should().BeFalse();
        retriedByType.Should().Be(0);
        (await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None))
            .Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(FailedOutboxFilter.MaxPageSize + 1)]
    public async Task RefuseAPageSizeOutOfRange(int pageSize)
    {
        // Arrange — SQL Server refuses a FETCH of no rows, and a page is not a way to read the whole table
        var filter = new FailedOutboxFilter { PageSize = pageSize };

        // Act
        var throughTheOutbox = () => this.outbox.QueryFailedAsync(filter, CancellationToken.None);
        var throughTheStore = () => this.store.QueryFailedAsync(filter, CancellationToken.None);

        // Assert
        await throughTheOutbox.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await throughTheStore.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task FindTheFailedEntriesPageByPage()
    {
        // Arrange — three failed entries beside a pending one
        var failedIds = new[]
        {
            await this.SaveFailedEntryAsync("msg-1", OrderPlaced),
            await this.SaveFailedEntryAsync("msg-2", OrderShipped),
            await this.SaveFailedEntryAsync("msg-3", OrderPlaced),
        };
        await this.SaveEntryAsync("msg-pending", OrderPlaced);

        // Act
        var first = await this.outbox.QueryFailedAsync(new FailedOutboxFilter { PageSize = 2 }, CancellationToken.None);
        var second = await this.outbox.QueryFailedAsync(
            new FailedOutboxFilter { PageSize = 2, PageToken = first.NextPageToken },
            CancellationToken.None);
        var placed = await this.outbox.QueryFailedAsync(
            new FailedOutboxFilter { MessageType = OrderPlaced },
            CancellationToken.None);

        // Assert — oldest first, with what a caller needs to decide what to retry
        first.TotalCount.Should().Be(3);
        first.Entries.Select(e => e.Id).Should().Equal(failedIds[0], failedIds[1]);
        first.NextPageToken.Should().NotBeNull();
        second.Entries.Select(e => e.Id).Should().Equal(failedIds[2]);
        second.NextPageToken.Should().BeNull();
        placed.Entries.Select(e => e.MessageId).Should().Equal("msg-1", "msg-3");
        placed.TotalCount.Should().Be(2);
        var entry = first.Entries[0];
        entry.Status.Should().Be(OutboxEntryStatus.Failed);
        entry.Type.Should().Be(OrderPlaced);
        entry.Payload.Should().Be("{}");
        entry.RetryCount.Should().Be(1);
    }

    [TestMethod]
    public void BeRegisteredByAddOutbox()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.AutoCreateTable = false)));

        // Assert
        services.Should().Contain(d =>
            d.ServiceType == typeof(IOutbox)
            && d.ImplementationType == typeof(OutboxOperations)
            && d.Lifetime == ServiceLifetime.Scoped);
    }

    private async Task SaveEntryAsync(string messageId, string type)
    {
        await this.connectionFactory.BeginTransactionAsync();
        await this.store.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = messageId,
                    ConversationId = "conv-1",
                    Type = type,
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            this.connectionFactory.GetCurrentTransaction()!,
            CancellationToken.None);
        await this.connectionFactory.CommitAsync();
    }

    /// <summary>
    /// Saves an entry and fails it the way the relay does: one failed attempt released for another, then the attempt
    /// that runs out of retries. Its next attempt is then pushed an hour out, so only a retry that resets it makes the
    /// entry due.
    /// </summary>
    private async Task<long> SaveFailedEntryAsync(string messageId, string type)
    {
        await this.SaveEntryAsync(messageId, type);
        var first = (await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None))
            .Single(e => e.MessageId == messageId);
        await this.store.IncrementRetryAsync(first.Id, first.ClaimToken!, TimeSpan.Zero, CancellationToken.None);
        var second = (await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None))
            .Single(e => e.MessageId == messageId);
        await this.store.MarkFailedAsync(second.Id, second.ClaimToken!, CancellationToken.None);

        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "UPDATE conduit_outbox SET NextAttemptAt = @NextAttemptAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@NextAttemptAt", DateTime.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@Id", second.Id);
        await command.ExecuteNonQueryAsync();
        return second.Id;
    }
}
