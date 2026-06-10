namespace Conduit.Transport.AmazonSqs.Tests.Integration;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static async Task Initialize(TestContext context)
    {
        await SqsTestBase.InitializeContainer();
        await SqsTestBase.ReapProjectResourcesAsync();
    }

    [AssemblyCleanup]
    public static Task Cleanup() => SqsTestBase.CleanupContainer();
}
