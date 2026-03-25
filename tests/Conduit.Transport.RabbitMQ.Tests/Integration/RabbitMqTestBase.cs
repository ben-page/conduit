using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Base class for RabbitMQ integration tests. Manages the RabbitMQ container lifecycle (static)
/// and provides factory methods for creating test hosts.
/// </summary>
[TestClass]
public abstract class RabbitMqTestBase
{
    private static RabbitMqContainer? container;
    private static string? sharedPrefix;
    private static bool isAvailable;

    protected static string? ConnectionString { get; private set; }
    protected static string TopologyPrefix => GetSharedPrefix();

    public static async Task InitializeContainer()
    {
        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=rabbitmq");

            var existing = await ContainerHelper.TryReuseContainerAsync("conduit-test=rabbitmq", 5672);
            if (existing is not null)
            {
                ConnectionString = $"amqp://guest:guest@{existing.Value.Hostname}:{existing.Value.Port}";
                isAvailable = true;
                return;
            }

            container = new RabbitMqBuilder("rabbitmq:4-management-alpine")
                .WithLabel("conduit-test", "rabbitmq")
                .Build();
            await container.StartAsync();
            ConnectionString = container.GetConnectionString();
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            ConnectionString = null;
            isAvailable = false;
            Console.WriteLine($"RabbitMQ container not available: {exception.Message}");
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
            Assert.Inconclusive("RabbitMQ container not available. Ensure Docker/Podman is running.");
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
                    endpoint.UseRabbitMQ(ConnectionString!));
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
        Action<RabbitMQOptions>? configureOptions = null)
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
                    endpoint.UseRabbitMQ(ConnectionString!, configureOptions));
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
