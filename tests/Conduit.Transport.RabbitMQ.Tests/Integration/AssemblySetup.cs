namespace Conduit.Transport.RabbitMQ.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    public static RabbitMqFixture Fixture { get; } = new();

    [AssemblyInitialize]
    public static async Task Initialize(TestContext context)
    {
        await Fixture.InitializeAsync();
    }

    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        await Fixture.CleanupAsync();
    }
}
