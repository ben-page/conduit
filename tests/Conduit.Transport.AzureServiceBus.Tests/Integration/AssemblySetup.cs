// Force sequential execution of ASB test classes. The ASB emulator has a hard cap of
// 50 entities per namespace. Parallel class execution races topic creation faster than
// the [TestCleanup] hook can delete topics, causing quota exhaustion mid-run.
[assembly: DoNotParallelize]

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext context) => AsbTestBase.InitializeContainer();

    [AssemblyCleanup]
    public static Task Cleanup() => AsbTestBase.CleanupContainer();
}
