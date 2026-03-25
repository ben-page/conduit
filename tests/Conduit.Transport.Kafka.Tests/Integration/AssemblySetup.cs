namespace Conduit.Transport.Kafka.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    public static KafkaFixture Fixture { get; } = new();

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
