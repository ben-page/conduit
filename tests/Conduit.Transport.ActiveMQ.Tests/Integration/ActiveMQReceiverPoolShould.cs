using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ;
using Conduit.Transport.ActiveMQ.Tests.Support;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests ActiveMQ Artemis receiver pool configuration: pool-assigned types route to dedicated
/// queues with independent prefetch settings, while unassigned types go to the default queue.
/// </summary>
[TestClass]
public class ActiveMQReceiverPoolShould : ActiveMQTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.ActiveMQ.Tests.Support.Contracts";

    private const string SlowCommandTypeName =
        "Conduit.Transport.ActiveMQ.Tests.Support.Contracts.SlowCommand";

    /// <summary>
    /// Creates a handler host with a named receiver pool that routes <paramref name="pooledTypeName"/>
    /// (SlowCommand unless given) to a dedicated queue with the specified prefetch count.
    /// </summary>
    private static async Task<TestHost> CreateHostWithPoolAsync(
        string applicationName,
        string poolName,
        int poolConcurrency,
        string? topologyPrefix = null,
        string pooledTypeName = SlowCommandTypeName)
    {
        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;

                if (topologyPrefix is not null)
                {
                    conduit.TopologyPrefix = topologyPrefix;
                }

                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseActiveMQ(ConnectionString, options =>
                    {
                        options.AddPool(poolName, pool =>
                        {
                            pool.Concurrency = poolConcurrency;
                            pool.Handle(pooledTypeName);
                        });
                    }));
            });
        });
    }

    [TestMethod]
    public async Task RouteMessageToCorrectPool()
    {
        // Arrange
        var handlerName = UniqueAppName("pool");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — SlowCommand is assigned to the "slow" pool
        var result = await mediator.ExecuteAsync(new SlowCommand("pooled", 100));

        // Assert — handler processed it via the pool queue
        result.Value.Should().Be("slow:pooled");
        await handlerSpy.WaitForCountAsync(1);
        handlerSpy.Invocations.OfType<SlowCommand>().Should().Contain(
            command => command.Input == "pooled");
    }

    [TestMethod]
    public async Task RouteUnassignedTypesToDefaultPool()
    {
        // Arrange
        var handlerName = UniqueAppName("pool");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — TestCommand is NOT in any pool, goes to default queue
        var result = await mediator.ExecuteAsync(new TestCommand("default"));

        // Assert — handler processed it via the default queue
        result.Value.Should().Be("handled:default");
        await handlerSpy.WaitForCountAsync(1);
        handlerSpy.Invocations.OfType<TestCommand>().Should().Contain(
            command => command.Input == "default");
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task IsolatePoolConcurrency()
    {
        // Arrange
        var handlerName = UniqueAppName("pool");
        var senderName = UniqueAppName("sender");

        // Slow pool with Concurrency=1 (prefetch), default pool uses DefaultConcurrency
        await using var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — send a slow command (pool queue, Concurrency=1) and a fast command concurrently
        var slowTask = mediator.ExecuteAsync(new SlowCommand("slow", 2000));
        var fastResult = await mediator.ExecuteAsync(new TestCommand("fast"));

        // Assert — fast command completes while slow is still processing
        fastResult.Value.Should().Be("handled:fast");

        // Wait for slow to complete
        var slowResult = await slowTask;
        slowResult.Value.Should().Be("slow:slow");

        // Both were handled
        await handlerSpy.WaitForCountAsync(2, TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task DeliverAPooledEventOnce()
    {
        // Arrange — TestEvent belongs to a pool; the application's other events stay on its own queue
        var handlerName = UniqueAppName("pool");
        var publisherName = UniqueAppName("pub");

        await using var handlerHost = await CreateHostWithPoolAsync(
            handlerName, "events", 2, pooledTypeName: typeof(TestEvent).FullName!);
        var prefix = handlerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(
            publisherName, prefix, handling => handling.ClearAllHandlers());

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var handlerOptions = handlerHost.GetRequiredService<ConduitOptions>();
        var appEventQueueName = TopologyNaming.CreateAppResourceName(handlerOptions, 256);
        var poolEventQueueName = TopologyNaming.CreateAppResourceName(handlerOptions, "events", 256);

        // Act
        await publisherHost.GetRequiredService<IMediator>().PublishAsync(new TestEvent("pooled"));
        await handlerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));

        // Assert — the broker routed it to the pool's queue alone: with no copy on the application's
        // own queue, no second delivery can follow the one the listener ran
        await using var management = await ArtemisManagement.ConnectAsync(ConnectionString!);
        (await management.MessagesAddedAsync(poolEventQueueName)).Should().Be(1);
        (await management.MessagesAddedAsync(appEventQueueName)).Should().Be(0);
        handlerSpy.Invocations.OfType<TestEvent>().Should().ContainSingle();
    }
}
