using System.Diagnostics;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.Kafka.Tests.Support;
using Conduit.Transport.Kafka.Tests.Support.Contracts;
using Conduit.Transport.Kafka;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Kafka.Tests.Integration;

/// <summary>
/// Tests the Kafka receiver pool model: each pool is a dedicated consumer group that exclusively
/// owns its assigned message types, while the default group owns the complement (types not claimed
/// by any pool). Concurrency is honoured by running that many consumer instances per group, so a
/// group with concurrency &gt; 1 processes the multi-partition shared topic in parallel.
/// </summary>
/// <remarks>
/// These assertions are designed to FAIL against the previous no-op implementation, in which a
/// single shared command/query consumer processed every type sequentially:
/// <list type="bullet">
/// <item><see cref="IsolatePoolConcurrency"/> — a blocking handler in the pool would have stalled
/// the single shared consumer, so the default-group command could not have completed while the
/// pool command was still in flight.</item>
/// <item><see cref="HonorPoolConcurrencyWithParallelExecution"/> — a single sequential consumer can
/// never observe more than one handler executing at a time, so the observed peak concurrency would
/// have been 1.</item>
/// </list>
/// </remarks>
[TestClass]
public class KafkaReceiverPoolShould : KafkaTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.Kafka.Tests.Support.Contracts";

    private const string SlowCommandTypeName =
        "Conduit.Transport.Kafka.Tests.Support.Contracts.SlowCommand";

    private const string ParallelProbeCommandTypeName =
        "Conduit.Transport.Kafka.Tests.Support.Contracts.ParallelProbeCommand";

    /// <summary>
    /// Creates a handler host with a named receiver pool that routes the given message types to a
    /// dedicated consumer group with the specified instance count (<paramref name="poolConcurrency"/>).
    /// </summary>
    private static async Task<TestHost> CreateHostWithPoolAsync(
        string applicationName,
        string poolName,
        int poolConcurrency,
        IReadOnlyList<string> pooledTypes,
        int defaultConcurrency = 1,
        string? topologyPrefix = null)
    {
        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddSingleton<ConcurrencyProbe>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;

                if (topologyPrefix is not null)
                {
                    conduit.TopologyPrefix = topologyPrefix;
                }

                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseKafka(BootstrapServers, options =>
                    {
                        options.DefaultConcurrency = defaultConcurrency;
                        options.AddPool(poolName, pool =>
                        {
                            pool.Concurrency = poolConcurrency;
                            foreach (var pooledType in pooledTypes)
                            {
                                pool.Handle(pooledType);
                            }
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

        await using var handlerHost = await CreateHostWithPoolAsync(
            handlerName, "slow", 1, [SlowCommandTypeName]);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — SlowCommand is assigned to the "slow" pool group
        var result = await mediator.ExecuteAsync(new SlowCommand("pooled", 100));

        // Assert — handler processed it via the pool consumer group
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

        await using var handlerHost = await CreateHostWithPoolAsync(
            handlerName, "slow", 1, [SlowCommandTypeName]);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — TestCommand is NOT in any pool, so it is owned by the default group only
        var result = await mediator.ExecuteAsync(new TestCommand("default"));

        // Assert — handler processed it via the default consumer group
        result.Value.Should().Be("handled:default");
        await handlerSpy.WaitForCountAsync(1);
        handlerSpy.Invocations.OfType<TestCommand>().Should().Contain(
            command => command.Input == "default");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task IsolatePoolConcurrency()
    {
        // Arrange — the "slow" pool owns SlowCommand on its own consumer group (Concurrency=1).
        // The default group owns TestCommand. The two groups are independent, so a blocking
        // handler on the pool must NOT delay the default group.
        var handlerName = UniqueAppName("pool");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostWithPoolAsync(
            handlerName, "slow", 1, [SlowCommandTypeName]);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — start a long-running pool command, then send a default-group command. The default
        // command must complete while the pool command is still being handled. Measure each
        // command's completion time from a shared start instant.
        var stopwatch = Stopwatch.StartNew();

        var slowTask = Task.Run(async () =>
        {
            var slowResult = await mediator.ExecuteAsync(new SlowCommand("slow", 8000));
            return (slowResult, elapsed: stopwatch.Elapsed);
        });

        // Give the pool command a moment to be picked up and start blocking its handler.
        await Task.Delay(1500);

        await mediator.ExecuteAsync(new TestCommand("fast"));
        var fastElapsed = stopwatch.Elapsed;

        var (slow, slowElapsed) = await slowTask;

        // Assert — the default-group command completed well before the pool command. With the old
        // single sequential consumer, the 8s SlowCommand handler would have blocked the shared
        // consumer and TestCommand could not have completed first.
        slow.Value.Should().Be("slow:slow");
        fastElapsed.Should().BeLessThan(
            slowElapsed - TimeSpan.FromSeconds(3),
            "the default group must process TestCommand while the pool is blocked on SlowCommand");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task HonorPoolConcurrencyWithParallelExecution()
    {
        // Arrange — pool owns ParallelProbeCommand with Concurrency=4. The shared topic has
        // enough partitions (6) to host 4 parallel consumer instances. Outbound messages are keyed
        // by a per-message id so same-type messages distribute across partitions.
        const int poolConcurrency = 4;
        const int commandCount = 8;
        const int holdMs = 3000;

        var handlerName = UniqueAppName("pool");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostWithPoolAsync(
            handlerName, "probe", poolConcurrency, [ParallelProbeCommandTypeName]);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var probe = handlerHost.GetRequiredService<ConcurrencyProbe>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — fire commandCount probe commands without awaiting individually, so they are
        // in flight together. Each handler holds for holdMs while recording concurrency.
        var tasks = new List<Task>();
        for (var index = 0; index < commandCount; index++)
        {
            var input = $"p{index}";
            tasks.Add(mediator.ExecuteAsync(new ParallelProbeCommand(input, holdMs)));
        }

        await Task.WhenAll(tasks);
        await handlerSpy.WaitForCountAsync(commandCount, TimeSpan.FromSeconds(30));

        // Assert — multiple handlers executed simultaneously. A single sequential consumer (the old
        // behaviour) could never exceed an observed concurrency of 1. With 4 consumer instances in
        // the pool group, we expect a peak of at least 2 (typically up to 4, bounded by partition
        // assignment and message distribution).
        probe.MaxObservedConcurrency.Should().BeGreaterThan(
            1,
            "a pool consumer group with Concurrency=4 must process partitions in parallel");
    }
}
