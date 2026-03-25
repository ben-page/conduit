namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext context) => AsbTestBase.InitializeContainer();

    [AssemblyCleanup]
    public static Task Cleanup() => AsbTestBase.CleanupContainer();
}
