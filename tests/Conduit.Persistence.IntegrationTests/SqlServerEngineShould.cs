using System.Diagnostics.CodeAnalysis;
using Conduit.Persistence;
using Conduit.Persistence.SqlServer;
using Conduit.TestUtils;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Runs the full persistence integration matrix against a real SQL Server container. The SQL Server
/// image is large and slow to start, so the container build allows a generous startup window.
/// </summary>
[TestClass]
public sealed class SqlServerEngineShould : PersistenceEngineTestBase
{
    private static MsSqlContainer? container;
    internal static string? BaseConnectionString { get; private set; }

    internal static bool IsContainerAvailable { get; private set; }

    protected override bool IsAvailable => IsContainerAvailable;

    protected override ISqlDialect Dialect { get; } = new SqlServerDialect();

    public static async Task InitializeContainer()
    {
        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=persistence-sqlserver");

            container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithLabel("conduit-test", "persistence-sqlserver")
                .Build();
            await container.StartAsync();
            BaseConnectionString = container.GetConnectionString();
            IsContainerAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            IsContainerAvailable = false;
            Console.WriteLine($"SQL Server container not available: {exception.Message}");
        }
    }

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

        await using (var admin = new SqlConnection(BaseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(BaseConnectionString) { InitialCatalog = databaseName };
        return new SqlServerConduitConnectionFactory(builder.ConnectionString);
    }
}
