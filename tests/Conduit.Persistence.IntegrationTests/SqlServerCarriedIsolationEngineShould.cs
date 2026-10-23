using System.Data;
using System.Diagnostics.CodeAnalysis;
using Conduit.Persistence;
using Conduit.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Runs the full persistence integration matrix on SQL Server sessions that carry Snapshot, the level a host's
/// own transactions leave behind on a pooled session. Every Conduit SQL Server statement must hold whatever
/// level a pooled session carries in; a statement that depends on the session's level fails here, not in a
/// host (DMS-6190). It reuses the container <see cref="SqlServerEngineShould"/> starts.
/// </summary>
[TestClass]
public sealed class SqlServerCarriedIsolationEngineShould : PersistenceEngineTestBase
{
    protected override bool IsAvailable => SqlServerEngineShould.IsContainerAvailable;

    protected override ISqlDialect Dialect { get; } = new SqlServerDialect();

    // CA2100: the database name is a server-generated GUID, never user input — trusted test infra.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Database name is a GUID generated in-test, not user input.")]
    protected override async Task<IConduitConnectionFactory> CreateIsolatedFactoryAsync()
    {
        var databaseName = $"conduit_{Guid.NewGuid():N}";

        await using (var admin = new SqlConnection(SqlServerEngineShould.BaseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]; ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(SqlServerEngineShould.BaseConnectionString) { InitialCatalog = databaseName };
        return new IsolationLevelSettingConnectionFactory(
            new SqlServerConduitConnectionFactory(builder.ConnectionString),
            IsolationLevel.Snapshot);
    }
}
