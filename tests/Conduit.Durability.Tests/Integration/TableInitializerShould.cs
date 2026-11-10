using AwesomeAssertions;
using Conduit.Durability.Tests.Support;
using Conduit.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// Replicas of one deployment start together on an empty database. On SQLite, which runs one writer at a time, every
/// create-if-absent statement is atomic; the server engines' race is covered by
/// <c>PersistenceEngineTestBase.TolerateAConcurrentCreate</c> in the persistence integration tests.
/// </summary>
[TestClass]
public sealed class TableInitializerShould
{
    [TestMethod]
    public async Task TolerateAConcurrentCreate()
    {
        // Arrange — eight replicas, each running every durable feature, on one fresh database file
        var path = Path.Combine(Path.GetTempPath(), $"conduit-race-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConduitConnectionFactory($"Data Source={path};Pooling=False");
            await using var provider = new ServiceCollection().BuildServiceProvider();
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = Enumerable.Range(0, 8)
                .Select(_ => new EveryStoreTableInitializer(provider, factory, new SqliteDialect()))
                .Select(async replica =>
                {
                    await go.Task;
                    await replica.StartAsync(CancellationToken.None);
                })
                .ToList();

            // Act
            go.SetResult();
            var act = () => Task.WhenAll(starts);

            // Assert — every replica starts, and the outbox has every index
            await act.Should().NotThrowAsync();
            await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'conduit_outbox' AND sql IS NOT NULL";
            var indexNames = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    indexNames.Add(reader.GetString(0));
                }
            }

            indexNames.Should().BeEquivalentTo(
                "ix_conduit_outbox_status_next_attempt",
                "ix_conduit_outbox_claim",
                "ix_conduit_outbox_message_id",
                "ix_conduit_outbox_status_sent");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
