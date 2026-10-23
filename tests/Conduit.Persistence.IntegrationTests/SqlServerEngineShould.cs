using System.Diagnostics.CodeAnalysis;
using Conduit.Persistence;
using Conduit.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Runs the full persistence integration matrix against a real SQL Server container. The SQL Server
/// image is large and slow to start, so the container build allows a generous startup window.
/// </summary>
[TestClass]
public sealed class SqlServerEngineShould : PersistenceEngineTestBase
{
    protected override bool IsAvailable => SqlServerContainer.IsAvailable;

    protected override ISqlDialect Dialect { get; } = new SqlServerDialect();

    // CA2100: the database name is a server-generated GUID, never user input — trusted test infra.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Database name is a GUID generated in-test, not user input.")]
    protected override async Task<IConduitConnectionFactory> CreateIsolatedFactoryAsync()
    {
        var databaseName = $"conduit_{Guid.NewGuid():N}";

        await using (var admin = new SqlConnection(SqlServerContainer.BaseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(SqlServerContainer.BaseConnectionString) { InitialCatalog = databaseName };
        return new SqlServerConduitConnectionFactory(builder.ConnectionString);
    }
}
