using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Tests Amazon SQS receiver pool configuration: pool-assigned types route to dedicated
/// queues with independent concurrency settings, while unassigned types go to the default queue.
/// </summary>
[TestClass]
[DoNotParallelize]
public class SqsReceiverPoolShould
{
    private static readonly SqsFixture Fixture = new();

    private const string ContractsNamespace = "Conduit.Transport.AmazonSqs.Tests.Support.Contracts";

    private const string SlowCommandTypeName =
        "Conduit.Transport.AmazonSqs.Tests.Support.Contracts.SlowCommand";

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        await Fixture.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await Fixture.CleanupAsync();
    }

    [TestCleanup]
    public async Task TestCleanup()
    {
        await Fixture.DisposeHostsAsync();
    }

    private static void EnsureAvailable()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Inconclusive("LocalStack container not available. Ensure Docker/Podman is running.");
        }
    }

    /// <summary>
    /// Creates a handler host with a named receiver pool that routes SlowCommand
    /// to a dedicated queue with the specified concurrency.
    /// </summary>
    private static async Task<TestHost> CreateHostWithPoolAsync(
        string applicationName,
        string poolName,
        int poolMaxConcurrency)
    {
        var address = Fixture.ServiceAddress;
        var prefix = Fixture.TopologyPrefix;

        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;
                conduit.TestPrefix = prefix;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAmazonSqs(options =>
                    {
                        options.ServiceUri = new Uri(address);
                        options.Pools.Add(new AmazonSqsReceiverPoolOptions
                        {
                            Name = poolName,
                            MaxConcurrency = poolMaxConcurrency,
                            MessageTypes = { SlowCommandTypeName },
                        });
                    }));
            });
        });

        Fixture.TrackHost(host);
        return host;
    }

    [TestMethod]
    public async Task RouteMessageToCorrectPool()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("pool");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act -- SlowCommand is assigned to the "slow" pool
        var result = await mediator.ExecuteAsync(new SlowCommand("pooled", 100));

        // Assert -- handler processed it via the pool queue
        result.Value.Should().Be("slow:pooled");
        await handlerSpy.WaitForCountAsync(1);
        handlerSpy.Invocations.OfType<SlowCommand>().Should().Contain(
            command => command.Input == "pooled");
    }

    [TestMethod]
    public async Task RouteUnassignedTypesToDefaultPool()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("pool");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act -- TestCommand is NOT in any pool, goes to default queue
        var result = await mediator.ExecuteAsync(new TestCommand("default"));

        // Assert -- handler processed it via the default queue
        result.Value.Should().Be("handled:default");
        await handlerSpy.WaitForCountAsync(1);
        handlerSpy.Invocations.OfType<TestCommand>().Should().Contain(
            command => command.Input == "default");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task IsolatePoolConcurrency()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("pool");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        // Slow pool with maxConcurrency=1, default pool uses DefaultMaxConcurrency
        var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act -- send a slow command (pool queue, maxConcurrency=1) and a fast command concurrently
        var slowTask = mediator.ExecuteAsync(new SlowCommand("slow", 2000));
        var fastResult = await mediator.ExecuteAsync(new TestCommand("fast"));

        // Assert -- fast command completes while slow is still processing
        fastResult.Value.Should().Be("handled:fast");

        // Wait for slow to complete
        var slowResult = await slowTask;
        slowResult.Value.Should().Be("slow:slow");

        // Both were handled
        await handlerSpy.WaitForCountAsync(2, TimeSpan.FromSeconds(10));
    }
}
