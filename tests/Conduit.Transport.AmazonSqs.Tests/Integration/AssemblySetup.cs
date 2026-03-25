namespace Conduit.Transport.AmazonSqs.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static Task Initialize(TestContext context) => SqsTestBase.InitializeContainer();

    [AssemblyCleanup]
    public static Task Cleanup() => SqsTestBase.CleanupContainer();
}
