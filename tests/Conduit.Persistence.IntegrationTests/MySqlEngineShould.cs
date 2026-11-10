using System.Diagnostics.CodeAnalysis;
using Conduit.Persistence;
using Conduit.Persistence.MySql;
using Conduit.TestUtils;
using MySqlConnector;
using Testcontainers.MySql;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Runs the full persistence integration matrix against a real MySQL 8 container.
/// </summary>
[TestClass]
public sealed class MySqlEngineShould : PersistenceEngineTestBase
{
    private static MySqlContainer? container;
    private static string? baseConnectionString;
    private static bool isAvailable;

    protected override bool IsAvailable => isAvailable;

    protected override ISqlDialect Dialect { get; } = new MySqlDialect();

    protected override string OutboxIndexNamesSql => """
        SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'conduit_outbox' AND INDEX_NAME <> 'PRIMARY'
        """;

    protected override bool MissingRowUpdateLocksTheGap => true;

    public static async Task InitializeContainer()
    {
        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=persistence-mysql");

            // Connect as root so the per-test CREATE DATABASE and the subsequent connection to that
            // fresh database both have full privileges. The Testcontainers default 'mysql' user is
            // only granted the single seeded database, so it cannot use databases we create per test.
            container = new MySqlBuilder("mysql:8.4")
                .WithUsername("root")
                .WithLabel("conduit-test", "persistence-mysql")
                .Build();
            await container.StartAsync();
            baseConnectionString = container.GetConnectionString();
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            isAvailable = false;
            Console.WriteLine($"MySQL container not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Closes the idle connections the test left pooled. Every test has a database of its own, so its
    /// pool is never reused, and the overlap-lock tests open twenty connections at once: left to idle
    /// out, a run's pools exhaust the server's connection limit partway through.
    /// </summary>
    [TestCleanup]
    public void ReleasePooledConnections() => MySqlConnection.ClearAllPools();

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
        var databaseName = $"conduit_{Guid.NewGuid():N}";

        await using (var admin = new MySqlConnection(baseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{databaseName}`";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new MySqlConnectionStringBuilder(baseConnectionString!) { Database = databaseName };
        return new MySqlConduitConnectionFactory(builder.ConnectionString);
    }
}
