using AwesomeAssertions;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Tests.Support;
using Conduit.TestUtils;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class DbConnectionInboxStoreShould : IAsyncDisposable
{
    private StubConnectionFactory factory = null!;
    private DbConnectionInboxStore store = null!;

    [TestInitialize]
    public async Task Setup()
    {
        this.factory = new StubConnectionFactory();
        this.store = new DbConnectionInboxStore(this.factory, new SqliteDialect());
        await this.store.EnsureTableExistsAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await this.factory.DisposeAsync();
    }

    [TestMethod]
    public async Task ReturnFalseForUnknownMessageId()
    {
        // Act
        var exists = await this.store.ExistsAsync("unknown-id", CancellationToken.None);

        // Assert
        exists.Should().BeFalse();
    }

    [TestMethod]
    public async Task ReturnTrueAfterRecording()
    {
        // Arrange
        await this.store.RecordAsync("msg-001", CancellationToken.None);

        // Act
        var exists = await this.store.ExistsAsync("msg-001", CancellationToken.None);

        // Assert
        exists.Should().BeTrue();
    }

    [TestMethod]
    public async Task NotThrowOnDuplicateRecord()
    {
        // Arrange
        await this.store.RecordAsync("msg-001", CancellationToken.None);

        // Act — recording same ID again should not throw
        await this.store.RecordAsync("msg-001", CancellationToken.None);

        // Assert
        var exists = await this.store.ExistsAsync("msg-001", CancellationToken.None);
        exists.Should().BeTrue();
    }

    [TestMethod]
    public async Task DeleteExpiredEntries()
    {
        // Arrange
        await this.store.RecordAsync("msg-old", CancellationToken.None);

        // Act — zero retention means everything is expired
        await this.store.DeleteExpiredAsync(TimeSpan.Zero, CancellationToken.None);

        // Assert
        var exists = await this.store.ExistsAsync("msg-old", CancellationToken.None);
        exists.Should().BeFalse();
    }

    [TestMethod]
    public async Task PreserveFreshEntriesDuringCleanup()
    {
        // Arrange
        await this.store.RecordAsync("msg-fresh", CancellationToken.None);

        // Act — 30 minute retention means the just-recorded entry survives
        await this.store.DeleteExpiredAsync(TimeSpan.FromMinutes(30), CancellationToken.None);

        // Assert
        var exists = await this.store.ExistsAsync("msg-fresh", CancellationToken.None);
        exists.Should().BeTrue();
    }
}
