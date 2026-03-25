using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Base class for ActiveMQ Artemis integration tests. Manages the Artemis container lifecycle (static)
/// and provides factory methods for creating test hosts.
/// </summary>
[TestClass]
public abstract class ActiveMQTestBase
{
    private static IContainer? container;
    private static string? sharedPrefix;
    private static bool isAvailable;

    protected static string? ConnectionString { get; private set; }
    protected static string TopologyPrefix => GetSharedPrefix();

    public static async Task InitializeContainer()
    {
        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=activemq");

            var existing = await ContainerHelper.TryReuseContainerAsync("conduit-test=activemq", 61616);
            if (existing is not null)
            {
                ConnectionString = $"amqp://admin:admin@{existing.Value.Hostname}:{existing.Value.Port}";
                isAvailable = true;
                return;
            }

            container = new ContainerBuilder("apache/activemq-artemis:2.37.0-alpine")
                .WithPortBinding(61616, true)
                .WithEnvironment("ARTEMIS_USER", "admin")
                .WithEnvironment("ARTEMIS_PASSWORD", "admin")
                .WithLabel("conduit-test", "activemq")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged("AMQ241004"))
                .Build();
            await container.StartAsync();

            ConnectionString = $"amqp://admin:admin@{container.Hostname}:{container.GetMappedPublicPort(61616)}";
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            ConnectionString = null;
            isAvailable = false;
            Console.WriteLine($"ActiveMQ Artemis container not available: {exception.Message}");
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
            Assert.Inconclusive("ActiveMQ Artemis container not available. Ensure Docker/Podman is running.");
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
                    endpoint.UseActiveMQ(ConnectionString!));
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
        Action<ActiveMQOptions>? configureOptions = null)
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
                    endpoint.UseActiveMQ(ConnectionString!, configureOptions));
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
