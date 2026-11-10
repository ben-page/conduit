using AwesomeAssertions;
using Conduit.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Conduit.Durability.Tests.Unit;

/// <summary>
/// The engine connection factories hand the caller an open connection, so a connection that fails to open is theirs
/// to dispose. Proved on SQLite; the other three engines' factories share the pattern.
/// </summary>
[TestClass]
public sealed class ConnectionFactoryShould
{
    [TestMethod]
    public async Task DisposeOnOpenFailure()
    {
        // Arrange — a read-only database in a directory that does not exist cannot be opened
        var path = Path.Combine(Path.GetTempPath(), $"conduit-missing-{Guid.NewGuid():N}", "conduit.db");
        SqliteConnection? created = null;
        var disposed = false;
        var factory = new SqliteConduitConnectionFactory(() =>
        {
            created = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            created.Disposed += (_, _) => disposed = true;
            return created;
        });

        // Act
        var act = () => factory.CreateConnectionAsync(CancellationToken.None);

        // Assert — the open's own exception reaches the caller, and the connection it never received is disposed
        await act.Should().ThrowAsync<SqliteException>();
        created.Should().NotBeNull();
        disposed.Should().BeTrue("the caller never received the connection, so nothing else can dispose it");
    }
}
