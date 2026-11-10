using System.Diagnostics.CodeAnalysis;
using Conduit.Persistence;
using Conduit.Persistence.Postgres;
using Conduit.TestUtils;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Runs the full persistence integration matrix against a real PostgreSQL container.
/// </summary>
[TestClass]
public sealed class PostgresEngineShould : PersistenceEngineTestBase
{
    private static PostgreSqlContainer? container;
    private static string? baseConnectionString;
    private static bool isAvailable;

    protected override bool IsAvailable => isAvailable;

    protected override ISqlDialect Dialect { get; } = new PostgresDialect();

    protected override string OutboxIndexNamesSql =>
        "SELECT indexname FROM pg_indexes WHERE tablename = 'conduit_outbox' AND indexname <> 'conduit_outbox_pkey'";

    public static async Task InitializeContainer()
    {
        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=persistence-postgres");

            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithLabel("conduit-test", "persistence-postgres")
                .Build();
            await container.StartAsync();
            baseConnectionString = container.GetConnectionString();
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            isAvailable = false;
            Console.WriteLine($"PostgreSQL container not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Closes the idle connections the test left pooled. Every test has a database of its own, so its
    /// pool is never reused, and the overlap-lock tests open twenty connections at once: left to idle
    /// out, a run's pools exhaust the server's connection limit partway through.
    /// </summary>
    [TestCleanup]
    public void ReleasePooledConnections() => NpgsqlConnection.ClearAllPools();

    public static async Task CleanupContainer()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    // CA2100: the database name is a server-generated GUID, never user input — trusted test infra.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Database name is a GUID generated in-test, not user input.")]
    protected override async Task<IConduitConnectionFactory> CreateIsolatedFactoryAsync()
    {
        // Each test gets its own database so hard-coded table names never collide.
        var databaseName = $"conduit_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(baseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString!) { Database = databaseName };
        return new PostgresConduitConnectionFactory(builder.ConnectionString);
    }
}
