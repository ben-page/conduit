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
/// and provides factory methods for creating test hosts.
/// </summary>
[TestClass]
public abstract class KafkaTestBase
{
    private static KafkaContainer? container;
    private static string? sharedPrefix;
    private static bool isAvailable;

    protected static string? BootstrapServers { get; private set; }
    protected static string TopologyPrefix => GetSharedPrefix();

    public static async Task InitializeContainer()
    {
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

            container = new KafkaBuilder("confluentinc/cp-kafka:7.7.1")
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

    protected static async Task<TestHost> CreateHostAsync(
        string applicationName,
        Action<ConduitRegistrationOverrides>? configureHandling = null)
    {
        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;
                conduit.TopologyPrefix = TopologyPrefix;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseKafka(BootstrapServers!));
            });

            if (configureHandling is not null)
            {
                builder.Services.ConfigureConduitRegistrations(configureHandling);
            }
        });
    }

    protected static async Task<TestHost> CreateSenderHostAsync(
        string applicationName,
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
                conduit.TopologyPrefix = TopologyPrefix;
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

    private static string GetSharedPrefix()
    {
        sharedPrefix ??= new ConduitOptions { TopologyMode = TopologyMode.Test }.TopologyPrefix!;
        return sharedPrefix;
    }
}
