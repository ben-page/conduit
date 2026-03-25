namespace Conduit.Transport.Kafka.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static async Task Initialize(TestContext context)
    {
        await KafkaTestBase.InitializeContainer();
    }

    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        await KafkaTestBase.CleanupContainer();
    }
}
