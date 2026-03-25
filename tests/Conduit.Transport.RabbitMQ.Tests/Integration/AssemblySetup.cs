namespace Conduit.Transport.RabbitMQ.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext context) => RabbitMqTestBase.InitializeContainer();

    [AssemblyCleanup]
    public static Task Cleanup() => RabbitMqTestBase.CleanupContainer();
}
