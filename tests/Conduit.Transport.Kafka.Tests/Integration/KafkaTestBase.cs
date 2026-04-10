using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Kafka;

namespace Conduit.Transport.Kafka.Tests.Integration;

/// <summary>
/// Base class for Kafka integration tests. Manages the Kafka container lifecycle (static)
/// and provides factory methods for creating test hosts with per-test topology isolation.
/// </summary>
[TestClass]
public abstract class KafkaTestBase
{
    private static KafkaContainer? container;
    private static bool isAvailable;

    protected static string? BootstrapServers { get; private set; }

    public static async Task InitializeContainer()
    {
        var config = TestConfiguration.Load<KafkaTestBase>();

        var bootstrapServers = config.Get("Kafka", "BootstrapServers");
        if (!string.IsNullOrWhiteSpace(bootstrapServers))
        {
            BootstrapServers = bootstrapServers;
            isAvailable = true;
            return;
        }

        if (!config.UseTestContainer("Kafka"))
        {
            isAvailable = false;
            return;
        }

        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=kafka");

            var existing = await ContainerHelper.TryReuseContainerAsync("conduit-test=kafka", 9093);
            if (existing is not null)
            {
                BootstrapServers = $"{existing.Value.Hostname}:{existing.Value.Port}";
                isAvailable = true;
                return;
            }

            container = new KafkaBuilder("confluentinc/cp-kafka:7.9.6")
                .WithLabel("conduit-test", "kafka")
                .Build();
            await container.StartAsync();
            BootstrapServers = container.GetBootstrapAddress();
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            BootstrapServers = null;
            isAvailable = false;
            Console.WriteLine($"Kafka container not available: {exception.Message}");
        }
    }

    public static async Task CleanupContainer()
    {
        if (container is not null && !ContainerHelper.ReuseContainers)
        {
            await container.DisposeAsync();
        }
    }

    [TestInitialize]
    public void TestInitialize()
    {
        if (!isAvailable)
        {
            Assert.Inconclusive("Kafka container not available. Ensure Docker/Podman is running.");
        }
    }

    /// <summary>
    /// Creates a handler/listener host. When <paramref name="topologyPrefix"/> is null,
    /// a new unique prefix is auto-generated (use <see cref="TestHost.TopologyPrefix"/>
    /// to pass it to subsequent hosts in the same test).
    /// </summary>
    protected static async Task<TestHost> CreateHostAsync(
        string applicationName,
        string? topologyPrefix = null,
        Action<ConduitRegistrationOverrides>? configureHandling = null)
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
                    endpoint.UseKafka(BootstrapServers!));
            });

            if (configureHandling is not null)
            {
                builder.Services.ConfigureConduitRegistrations(configureHandling);
            }
        });
    }

    /// <summary>
    /// Creates a sender host with routing. Uses the provided <paramref name="topologyPrefix"/>
    /// to share topology with the handler host in the same test.
    /// </summary>
    protected static async Task<TestHost> CreateSenderHostAsync(
        string applicationName,
        string topologyPrefix,
        string routeNamespace,
        string targetEndpointName,
        Action<KafkaOptions>? configureOptions = null)
    {
        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.ProducerOnly = true;
                conduit.TopologyMode = TopologyMode.Test;
                conduit.TopologyPrefix = topologyPrefix;
                conduit.AddEndpoint(targetEndpointName, endpoint =>
                    endpoint.UseKafka(BootstrapServers!, configureOptions));
                conduit.Options.Routes.Add(new ConduitRoute
                {
                    Pattern = routeNamespace,
                    EndpointName = targetEndpointName,
                });
            });
        });
    }

    protected static string UniqueAppName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..15];
}
