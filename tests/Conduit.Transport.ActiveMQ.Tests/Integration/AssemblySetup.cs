namespace Conduit.Transport.ActiveMQ.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext context) => ActiveMQTestBase.InitializeContainer();

    [AssemblyCleanup]
    public static Task Cleanup() => ActiveMQTestBase.CleanupContainer();
}
