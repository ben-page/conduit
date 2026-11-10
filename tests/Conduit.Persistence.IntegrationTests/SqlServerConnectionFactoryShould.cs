using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AwesomeAssertions;
using Conduit.Persistence.SqlServer;
using Microsoft.Data.SqlClient;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// The outbox's and the jobs' terminal writes require exactly one row affected. A server whose <c>user options</c>
/// include <c>NOCOUNT</c> (512) starts every session at <c>NOCOUNT ON</c>, which reports -1 instead, and they would read
/// that as a lost claim. <see cref="SqlServerConduitConnectionFactory"/> turns row counting back on for every connection
/// it hands out. (A pooled session an earlier user left at <c>NOCOUNT ON</c> is not a case: the pool's reset restores
/// the login's default, measured on SQL Server 2022 with Microsoft.Data.SqlClient 7.1.1.)
/// </summary>
/// <remarks>
/// It changes a server-wide setting for the length of one test and restores it in a <c>finally</c>; this assembly runs
/// its tests one at a time, so no other test sees it.
/// </remarks>
[TestClass]
public sealed class SqlServerConnectionFactoryShould
{
    private const int NoCountOption = 512;

    [TestInitialize]
    public void TestInitialize()
    {
        if (!SqlServerContainer.IsAvailable)
        {
            Assert.Inconclusive("SqlServerConnectionFactoryShould: SQL Server container not available.");
        }
    }

    [TestMethod]
    public async Task CountRowsOnAServerWhoseSessionsStartAtNoCountOn()
    {
        // Arrange — a server whose new sessions start at NOCOUNT ON, and a pool no earlier session warmed
        var connectionString = await CreateProbeDatabaseAsync();
        var previousUserOptions = await ReadUserOptionsAsync();
        await SetUserOptionsAsync(previousUserOptions | NoCountOption);
        try
        {
            SqlConnection.ClearAllPools();
            await using (var raw = new SqlConnection(connectionString))
            {
                await raw.OpenAsync();
                (await ReadSessionOptionsAsync(raw) & NoCountOption).Should()
                    .Be(NoCountOption, "the server option reaches a session the factory did not open");
            }

            SqlConnection.ClearAllPools();
            var factory = new SqlServerConduitConnectionFactory(connectionString);

            // Act
            await using var connection = (SqlConnection)await factory.CreateConnectionAsync(CancellationToken.None);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE probe SET Value = Value + 1 WHERE Id = 1";
            var rowsAffected = await update.ExecuteNonQueryAsync();

            // Assert
            rowsAffected.Should().Be(1, "a guard on rows affected must not read -1");
        }
        finally
        {
            await SetUserOptionsAsync(previousUserOptions);
            SqlConnection.ClearAllPools();
        }
    }

    private static async Task<int> ReadUserOptionsAsync()
    {
        await using var admin = new SqlConnection(SqlServerContainer.BaseConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = "SELECT CONVERT(INT, value_in_use) FROM sys.configurations WHERE name = 'user options'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task SetUserOptionsAsync(int userOptions)
    {
        await using var admin = new SqlConnection(SqlServerContainer.BaseConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = "EXEC sp_configure 'user options', @UserOptions; RECONFIGURE;";
        command.Parameters.AddWithValue("@UserOptions", userOptions);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadSessionOptionsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@OPTIONS";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // CA2100: the database name is a GUID generated in-test, never user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Database name is a GUID generated in-test, not user input.")]
    private static async Task<string> CreateProbeDatabaseAsync()
    {
        var databaseName = $"nocount_probe_{Guid.NewGuid():N}";
        await using (var admin = new SqlConnection(SqlServerContainer.BaseConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}];";
            await command.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(SqlServerContainer.BaseConnectionString)
        {
            InitialCatalog = databaseName,
            ApplicationName = $"nocount-probe-{Guid.NewGuid():N}",
        };
        await using (var setup = new SqlConnection(builder.ConnectionString))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE probe (Id INT PRIMARY KEY, Value INT NOT NULL); INSERT INTO probe VALUES (1, 0);";
            await command.ExecuteNonQueryAsync();
        }

        return builder.ConnectionString;
    }
}
