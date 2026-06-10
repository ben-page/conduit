using Conduit.Configuration;
using Conduit.Durability;
using Conduit.Persistence.Sqlite;
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
/// and provides factory methods for creating test hosts with per-test topology isolation.
/// </summary>
[TestClass]
public abstract class ActiveMQTestBase
{
    private static IContainer? container;
    private static bool isAvailable;

    protected static string? ConnectionString { get; private set; }

    public static async Task InitializeContainer()
    {
        var config = TestConfiguration.Load<ActiveMQTestBase>();

        var connectionString = config.GetConnectionString("ActiveMQ");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            ConnectionString = connectionString;
            isAvailable = true;
            return;
        }

        if (!config.UseTestContainer("ActiveMQ"))
        {
            isAvailable = false;
            return;
        }

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

            container = new ContainerBuilder("apache/activemq-artemis:2.44.0-alpine")
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
                    endpoint.UseActiveMQ(ConnectionString!));
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
                conduit.TopologyPrefix = topologyPrefix;
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

    /// <summary>
    /// Creates a host with the dead letter queue configured alongside ActiveMQ.
    /// </summary>
    protected static async Task<TestHost> CreateHostWithDeadLettersAsync(
        string applicationName,
        string dlqDatabasePath,
        string? topologyPrefix = null,
        Action<ConduitRegistrationOverrides>? configureHandling = null)
    {
        var connectionString = $"Data Source={dlqDatabasePath}";

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
                    endpoint.UseActiveMQ(ConnectionString!));

                conduit.UseSqlite(connectionString);
                conduit.AddDurability(durability =>
                {
                    durability.AddDeadLetterQueue();
                });
            });

            if (configureHandling is not null)
            {
                builder.Services.ConfigureConduitRegistrations(configureHandling);
            }
        });
    }

    /// <summary>Returns a unique temp file path for a DLQ test database.</summary>
    protected static string CreateDeadLetterDatabasePath()
        => Path.Combine(Path.GetTempPath(), $"conduit-dlq-test-{Guid.NewGuid():N}.db");
}
