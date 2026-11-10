using Conduit.TestUtils;
using Testcontainers.MsSql;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// The SQL Server container shared by every SQL Server test class in this assembly. <see cref="AssemblySetup"/>
/// starts and disposes it once; the classes read <see cref="BaseConnectionString"/> and <see cref="IsAvailable"/>.
/// </summary>
internal static class SqlServerContainer
{
    private static MsSqlContainer? container;

    internal static string? BaseConnectionString { get; private set; }

    internal static bool IsAvailable { get; private set; }

    /// <summary>The catalog query for the names of the indexes on <c>conduit_outbox</c>, its primary key left out.</summary>
    internal static string OutboxIndexNamesSql =>
        "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'conduit_outbox') AND is_primary_key = 0 AND name IS NOT NULL";

    internal static async Task InitializeAsync()
    {
        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=persistence-sqlserver");

            container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithLabel("conduit-test", "persistence-sqlserver")
                .Build();
            await container.StartAsync();
            BaseConnectionString = container.GetConnectionString();
            IsAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            IsAvailable = false;
            Console.WriteLine($"SQL Server container not available: {exception.Message}");
        }
    }

    internal static async Task CleanupAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
