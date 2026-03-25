using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ;
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
    /// Creates a handler host with a named receiver pool that routes SlowCommand
    /// to a dedicated queue with the specified prefetch count.
    /// </summary>
    private static async Task<TestHost> CreateHostWithPoolAsync(
        string applicationName,
        string poolName,
        int poolPrefetchCount,
        string? topologyPrefix = null)
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
                    endpoint.UseActiveMQ(ConnectionString!, options =>
                    {
                        options.Pools.Add(new ActiveMQReceiverPoolOptions
                        {
                            Name = poolName,
                            PrefetchCount = poolPrefetchCount,
                            MessageTypes = { SlowCommandTypeName },
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

        await Task.Delay(2000);

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

        await Task.Delay(2000);

        // Act — TestCommand is NOT in any pool, goes to default queue
        var result = await mediator.ExecuteAsync(new TestCommand("default"));

        // Assert — handler processed it via the default queue
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
        var handlerName = UniqueAppName("pool");
        var senderName = UniqueAppName("sender");

        // Slow pool with prefetch=1, default pool uses DefaultPrefetchCount
        await using var handlerHost = await CreateHostWithPoolAsync(handlerName, "slow", 1);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act — send a slow command (pool queue, prefetch=1) and a fast command concurrently
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
}
