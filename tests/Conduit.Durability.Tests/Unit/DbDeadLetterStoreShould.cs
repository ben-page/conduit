using AwesomeAssertions;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class DbDeadLetterStoreShould : IAsyncDisposable
{
    private StubConnectionFactory connectionFactory = null!;
    private DbDeadLetterStore store = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        this.connectionFactory = new StubConnectionFactory();
        this.store = new DbDeadLetterStore(this.connectionFactory, new SqliteDialect());
        await this.store.EnsureTableExistsAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await this.connectionFactory.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateTableOnEnsure()
    {
        // Arrange — table already created in SetUp
        var connection = this.connectionFactory.Connection;

        // Act
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='conduit_dead_letters'";
        var result = await command.ExecuteScalarAsync();

        // Assert
        result.Should().Be("conduit_dead_letters");
    }

    [TestMethod]
    public async Task WriteAndGetEntry()
    {
        // Arrange
        var entry = CreateEntry("Orders.Commands.CreateOrder");

        // Act
        await this.store.WriteAsync(entry, CancellationToken.None);
        var retrieved = await this.store.GetAsync(entry.Id, CancellationToken.None);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(entry.Id);
        retrieved.MessageType.Should().Be("Orders.Commands.CreateOrder");
        retrieved.SourceEndpoint.Should().Be("orders");
        retrieved.ExceptionType.Should().Be("System.InvalidOperationException");
        retrieved.ExceptionMessage.Should().Be("Something went wrong");
        retrieved.Reference.Should().Be("ref-1");
        retrieved.MessageId.Should().Be("msg-1");
        retrieved.ConversationId.Should().Be("conv-1");
        retrieved.AttemptCount.Should().Be(1);
        retrieved.Body.ToArray().Should().Equal(entry.Body.ToArray());
    }

    [TestMethod]
    public async Task ReturnNullForMissingEntry()
    {
        // Act
        var result = await this.store.GetAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task QueryByMessageType()
    {
        // Arrange
        await this.store.WriteAsync(CreateEntry("Orders.Commands.CreateOrder"), CancellationToken.None);
        await this.store.WriteAsync(CreateEntry("Orders.Commands.ShipOrder"), CancellationToken.None);
        await this.store.WriteAsync(CreateEntry("Orders.Commands.CreateOrder"), CancellationToken.None);

        // Act
        var page = await this.store.QueryAsync(
            new DeadLetterFilter { MessageType = "Orders.Commands.CreateOrder" },
            CancellationToken.None);

        // Assert
        page.Entries.Should().HaveCount(2);
        page.TotalCount.Should().Be(2);
        page.Entries.Should().OnlyContain(e => e.MessageType == "Orders.Commands.CreateOrder");
    }

    [TestMethod]
    public async Task QueryWithNoFilters()
    {
        // Arrange
        await this.store.WriteAsync(CreateEntry("A"), CancellationToken.None);
        await this.store.WriteAsync(CreateEntry("B"), CancellationToken.None);

        // Act
        var page = await this.store.QueryAsync(new DeadLetterFilter(), CancellationToken.None);

        // Assert
        page.Entries.Should().HaveCount(2);
        page.TotalCount.Should().Be(2);
    }

    [TestMethod]
    public async Task QueryWithPagination()
    {
        // Arrange — 5 entries with slightly different timestamps so ordering is deterministic
        for (var i = 0; i < 5; i++)
        {
            await this.store.WriteAsync(CreateEntry("Test.Command", offsetMs: -i * 10), CancellationToken.None);
        }

        // Act — first page
        var page1 = await this.store.QueryAsync(
            new DeadLetterFilter { PageSize = 2 },
            CancellationToken.None);

        // Assert
        page1.Entries.Should().HaveCount(2);
        page1.NextPageToken.Should().NotBeNull();
        page1.TotalCount.Should().Be(5);

        // Act — second page
        var page2 = await this.store.QueryAsync(
            new DeadLetterFilter { PageSize = 2, PageToken = page1.NextPageToken },
            CancellationToken.None);

        page2.Entries.Should().HaveCount(2);
        page2.NextPageToken.Should().NotBeNull();

        // Act — third page (last)
        var page3 = await this.store.QueryAsync(
            new DeadLetterFilter { PageSize = 2, PageToken = page2.NextPageToken },
            CancellationToken.None);

        page3.Entries.Should().HaveCount(1);
        page3.NextPageToken.Should().BeNull();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(DeadLetterFilter.MaxPageSize + 1)]
    public async Task RefuseAPageSizeOutOfRange(int pageSize)
    {
        // Act — SQL Server refuses a FETCH of no rows, and a page is not a way to read the whole table
        var act = () => this.store.QueryAsync(new DeadLetterFilter { PageSize = pageSize }, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task QueryByExceptionType()
    {
        // Arrange
        await this.store.WriteAsync(CreateEntry("Test.A", exceptionType: "System.InvalidOperationException"), CancellationToken.None);
        await this.store.WriteAsync(CreateEntry("Test.B", exceptionType: "System.ArgumentException"), CancellationToken.None);

        // Act
        var page = await this.store.QueryAsync(
            new DeadLetterFilter { ExceptionType = "System.ArgumentException" },
            CancellationToken.None);

        // Assert
        page.Entries.Should().HaveCount(1);
        page.Entries[0].MessageType.Should().Be("Test.B");
    }

    [TestMethod]
    public async Task FilterByInstantRegardlessOfOffset()
    {
        // Arrange — captured at noon UTC, the instant expressed in a UTC+2 offset
        var instant = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var entry = CreateEntry("Test.Command", deadLetteredAt: instant.ToOffset(TimeSpan.FromHours(2)));
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act — the same instant as both bounds, written as a UTC+1 and a UTC-5 caller would write it
        var page = await this.store.QueryAsync(
            new DeadLetterFilter
            {
                Since = instant.ToOffset(TimeSpan.FromHours(1)),
                Until = instant.ToOffset(TimeSpan.FromHours(-5)),
            },
            CancellationToken.None);

        // Assert — the bounds are instants: the entry captured at that instant is within both
        page.Entries.Should().ContainSingle().Which.DeadLetteredAt.Should().Be(instant);
    }

    [TestMethod]
    public async Task ClaimAnEntryForOneReplayAtATime()
    {
        // Arrange
        var entry = CreateEntry("Test.Command");
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act — two replays claim it, the first releases its claim, and a third claims it again
        var first = await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None);
        var second = await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None);
        await this.store.ReleaseReplayClaimAsync(entry.Id, first!, CancellationToken.None);
        var third = await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None);
        var missing = await this.store.ClaimForReplayAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        first.Should().NotBeNullOrEmpty();
        second.Should().BeNull("the first replay holds the entry");
        third.Should().NotBeNullOrEmpty().And.NotBe(first);
        missing.Should().BeNull();
    }

    [TestMethod]
    public async Task ReleaseOnlyTheClaimItIsGiven()
    {
        // Arrange — a replay holds the entry
        var entry = CreateEntry("Test.Command");
        await this.store.WriteAsync(entry, CancellationToken.None);
        var holder = await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None);

        // Act — a release under another token, as a replay whose claim was taken over would make
        await this.store.ReleaseReplayClaimAsync(entry.Id, "another-claim", CancellationToken.None);
        var whileHeld = await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None);

        // Assert
        holder.Should().NotBeNullOrEmpty();
        whileHeld.Should().BeNull("the holder's claim was not released");
    }

    [TestMethod]
    public async Task TakeOverAReplayClaimOnceItIsAbandoned()
    {
        // Arrange — a replay claims the entry and its process stops before it releases or removes it
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new DbDeadLetterStore(this.connectionFactory, new SqliteDialect(), clock);
        var entry = CreateEntry("Test.Command");
        await store.WriteAsync(entry, CancellationToken.None);
        await store.ClaimForReplayAsync(entry.Id, CancellationToken.None);

        // Act — a tick short of the five-minute claim timeout, then at it
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var whileHeld = await store.ClaimForReplayAsync(entry.Id, CancellationToken.None);
        clock.Advance(TimeSpan.FromTicks(1));
        var onceAbandoned = await store.ClaimForReplayAsync(entry.Id, CancellationToken.None);

        // Assert
        whileHeld.Should().BeNull();
        onceAbandoned.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task RemoveSingleEntry()
    {
        // Arrange
        var entry = CreateEntry("Test.Command");
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act
        await this.store.RemoveAsync(entry.Id, CancellationToken.None);

        // Assert
        var result = await this.store.GetAsync(entry.Id, CancellationToken.None);
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task RemoveMultipleEntries()
    {
        // Arrange
        var entry1 = CreateEntry("A");
        var entry2 = CreateEntry("B");
        var entry3 = CreateEntry("C");
        await this.store.WriteAsync(entry1, CancellationToken.None);
        await this.store.WriteAsync(entry2, CancellationToken.None);
        await this.store.WriteAsync(entry3, CancellationToken.None);

        // Act
        await this.store.RemoveAsync([entry1.Id, entry3.Id], CancellationToken.None);

        // Assert
        (await this.store.GetAsync(entry1.Id, CancellationToken.None)).Should().BeNull();
        (await this.store.GetAsync(entry2.Id, CancellationToken.None)).Should().NotBeNull();
        (await this.store.GetAsync(entry3.Id, CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task RemoveEmptyListIsNoOp()
    {
        // Arrange
        await this.store.WriteAsync(CreateEntry("Test"), CancellationToken.None);

        // Act
        await this.store.RemoveAsync([], CancellationToken.None);

        // Assert — still one entry
        var page = await this.store.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
        page.Entries.Should().HaveCount(1);
    }

    private static DeadLetterEntry CreateEntry(
        string messageType,
        int offsetMs = 0,
        string? exceptionType = "System.InvalidOperationException",
        DateTimeOffset? deadLetteredAt = null)
    {
        return new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            MessageType = messageType,
            Body = new BinaryData("""{"orderId":"abc"}"""u8.ToArray()),
            SourceEndpoint = "orders",
            ExceptionType = exceptionType,
            ExceptionMessage = "Something went wrong",
            Reference = "ref-1",
            MessageId = "msg-1",
            CausationId = null,
            ConversationId = "conv-1",
            AttemptCount = 1,
            DeadLetteredAt = deadLetteredAt ?? DateTimeOffset.UtcNow.AddMilliseconds(offsetMs),
        };
    }
}
