namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Starts all three engine containers (Postgres, SQL Server, MySQL) in parallel once per assembly,
/// and disposes them on cleanup. Each engine's availability is independent — an engine whose container
/// fails to start simply marks its own tests Inconclusive (via the base <c>[TestInitialize]</c> guard).
/// </summary>
[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext context) => Task.WhenAll(
        PostgresEngineShould.InitializeContainer(),
        SqlServerContainer.InitializeAsync(),
        MySqlEngineShould.InitializeContainer());

    [AssemblyCleanup]
    public static Task Cleanup() => Task.WhenAll(
        PostgresEngineShould.CleanupContainer(),
        SqlServerContainer.CleanupAsync(),
        MySqlEngineShould.CleanupContainer());
}
