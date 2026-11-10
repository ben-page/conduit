using AwesomeAssertions;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class DeadLettersShould
{
    private StubDeadLetterStore store = null!;
    private SpyTransport transport = null!;
    private DeadLetters deadLetters = null!;

    [TestInitialize]
    public void SetUp()
    {
        this.store = new StubDeadLetterStore();
        this.transport = new SpyTransport();

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConduitTransport>("orders", this.transport);
        var serviceProvider = services.BuildServiceProvider();

        this.deadLetters = new DeadLetters(
            this.store,
            serviceProvider,
            NullLogger<DeadLetters>.Instance);
    }

    [TestMethod]
    public async Task DelegateQueryToStore()
    {
        // Arrange
        await this.store.WriteAsync(CreateEntry("Test.Command", "orders"), CancellationToken.None);

        // Act
        var page = await this.deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);

        // Assert
        page.Entries.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task DelegateGetToStore()
    {
        // Arrange
        var entry = CreateEntry("Test.Command", "orders");
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act
        var result = await this.deadLetters.GetAsync(entry.Id, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(entry.Id);
    }

    [TestMethod]
    public async Task ReplaySingleMessage()
    {
        // Arrange
        var entry = CreateEntry("Test.Command", "orders");
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act
        var result = await this.deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        this.transport.Redelivered.Should().HaveCount(1);
        this.transport.Redelivered[0].Type.Should().Be("Test.Command");
        this.store.Entries.Should().BeEmpty("entry should be removed after successful replay");
    }

    [TestMethod]
    public async Task ReturnFailureWhenEntryNotFound()
    {
        // Act
        var result = await this.deadLetters.ReplayAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [TestMethod]
    public async Task ReturnFailureWhenTransportNotResolvable()
    {
        // Arrange — entry has endpoint "unknown" which is not registered
        var entry = CreateEntry("Test.Command", "unknown-endpoint");
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act
        var result = await this.deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        this.store.Entries.Should().HaveCount(1, "entry should NOT be removed on failure");
    }

    [TestMethod]
    public async Task ReleaseItsClaimWhenTheRedeliveryFails()
    {
        // Arrange — a transport that refuses the redelivery
        var entry = CreateEntry("Test.Command", "orders");
        await this.store.WriteAsync(entry, CancellationToken.None);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConduitTransport>("orders", new ThrowingTransport());
        await using var serviceProvider = services.BuildServiceProvider();
        var deadLetters = new DeadLetters(this.store, serviceProvider, NullLogger<DeadLetters>.Instance);

        // Act
        var result = await deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

        // Assert — the entry stays, and the next replay can claim it at once
        result.Success.Should().BeFalse();
        this.store.Entries.Should().ContainSingle();
        (await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task RefuseToReplayAnEntryAnotherReplayHolds()
    {
        // Arrange — another replay has claimed the entry and is redelivering it
        var entry = CreateEntry("Test.Command", "orders");
        await this.store.WriteAsync(entry, CancellationToken.None);
        await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None);

        // Act
        var result = await this.deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

        // Assert — refused, not redelivered a second time, and the entry is left to the replay that holds it
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already being replayed");
        this.transport.Redelivered.Should().BeEmpty();
        this.store.Entries.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ReportAReplayRedeliveredWhenOnlyTheRemovalFails()
    {
        // Arrange — a store that cannot remove the entry once it is redelivered
        var entry = CreateEntry("Test.Command", "orders");
        await this.store.WriteAsync(entry, CancellationToken.None);
        this.store.FailRemoval = true;

        // Act
        var result = await this.deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

        // Assert — the message was re-published; the entry stays claimed, so no replay re-publishes it until the claim
        // times out
        result.Success.Should().BeTrue();
        this.transport.Redelivered.Should().ContainSingle();
        this.store.Entries.Should().ContainSingle();
        (await this.store.ClaimForReplayAsync(entry.Id, CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task ReplayMultipleMessagesSequentially()
    {
        // Arrange
        var entry1 = CreateEntry("A", "orders");
        var entry2 = CreateEntry("B", "orders");
        await this.store.WriteAsync(entry1, CancellationToken.None);
        await this.store.WriteAsync(entry2, CancellationToken.None);

        // Act
        var results = await this.deadLetters.ReplayAsync([entry1.Id, entry2.Id], CancellationToken.None);

        // Assert
        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Success);
        this.transport.Redelivered.Should().HaveCount(2);
        this.store.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReplayByMessageType()
    {
        // Arrange
        var entry1 = CreateEntry("Target.Command", "orders");
        var entry2 = CreateEntry("Other.Command", "orders");
        var entry3 = CreateEntry("Target.Command", "orders");
        await this.store.WriteAsync(entry1, CancellationToken.None);
        await this.store.WriteAsync(entry2, CancellationToken.None);
        await this.store.WriteAsync(entry3, CancellationToken.None);

        // Act
        var results = await this.deadLetters.ReplayByTypeAsync("Target.Command", CancellationToken.None);

        // Assert
        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Success);
        this.store.Entries.Should().HaveCount(1);
        this.store.Entries[0].MessageType.Should().Be("Other.Command");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(DeadLetterFilter.MaxPageSize + 1)]
    public async Task RefuseAPageSizeOutOfRange(int pageSize)
    {
        // Act
        var act = () => this.deadLetters.QueryAsync(new DeadLetterFilter { PageSize = pageSize }, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task ReplayEveryEntryOfATypeAcrossPages()
    {
        // Arrange — more entries of the type than one page holds
        for (var index = 0; index <= DeadLetterFilter.MaxPageSize; index++)
        {
            await this.store.WriteAsync(CreateEntry("Target.Command", "orders"), CancellationToken.None);
        }

        // Act
        var results = await this.deadLetters.ReplayByTypeAsync("Target.Command", CancellationToken.None);

        // Assert
        results.Should().HaveCount(DeadLetterFilter.MaxPageSize + 1).And.OnlyContain(result => result.Success);
        this.store.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task StopReadingPagesWhenTheStoreHandsBackTheSameToken()
    {
        // Arrange — a custom store whose next page token never moves on
        var entry = CreateEntry("Target.Command", "orders");
        var repeatingStore = new RepeatingTokenDeadLetterStore(entry);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConduitTransport>("orders", this.transport);
        await using var serviceProvider = services.BuildServiceProvider();
        var deadLetters = new DeadLetters(repeatingStore, serviceProvider, NullLogger<DeadLetters>.Instance);

        // Act
        var results = await deadLetters.ReplayByTypeAsync("Target.Command", CancellationToken.None);

        // Assert
        results.Should().ContainSingle().Which.Success.Should().BeTrue();
        repeatingStore.Removed.Should().BeTrue();
    }

    [TestMethod]
    public async Task DiscardSingleEntry()
    {
        // Arrange
        var entry = CreateEntry("Test.Command", "orders");
        await this.store.WriteAsync(entry, CancellationToken.None);

        // Act
        await this.deadLetters.DiscardAsync(entry.Id, CancellationToken.None);

        // Assert
        this.store.Entries.Should().BeEmpty();
        this.transport.Redelivered.Should().BeEmpty("discard should not replay");
    }

    [TestMethod]
    public async Task DiscardMultipleEntries()
    {
        // Arrange
        var entry1 = CreateEntry("A", "orders");
        var entry2 = CreateEntry("B", "orders");
        await this.store.WriteAsync(entry1, CancellationToken.None);
        await this.store.WriteAsync(entry2, CancellationToken.None);

        // Act
        await this.deadLetters.DiscardAsync([entry1.Id, entry2.Id], CancellationToken.None);

        // Assert
        this.store.Entries.Should().BeEmpty();
    }

    private static DeadLetterEntry CreateEntry(string messageType, string sourceEndpoint)
    {
        return new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            MessageType = messageType,
            Body = new BinaryData("""{"data":"test"}"""u8.ToArray()),
            SourceEndpoint = sourceEndpoint,
            ExceptionType = "System.Exception",
            ExceptionMessage = "Test failure",
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = Guid.NewGuid().ToString("N"),
            AttemptCount = 1,
            DeadLetteredAt = DateTimeOffset.UtcNow,
        };
    }
}
