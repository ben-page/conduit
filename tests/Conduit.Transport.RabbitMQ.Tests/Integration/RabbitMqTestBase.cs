using Conduit.Configuration;
using Conduit.Durability;
using Conduit.Persistence.Sqlite;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Base class for RabbitMQ integration tests. Manages the RabbitMQ container lifecycle (static)
/// and provides factory methods for creating test hosts with per-test topology isolation.
/// </summary>
[TestClass]
public abstract class RabbitMqTestBase
{
    private static RabbitMqContainer? container;
    private static bool isAvailable;

    protected static string? ConnectionString { get; private set; }

    public static async Task InitializeContainer()
    {
        var config = TestConfiguration.Load<RabbitMqTestBase>();

        var connectionString = config.GetConnectionString("RabbitMQ");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            ConnectionString = connectionString;
            isAvailable = true;
            return;
        }

        if (!config.UseTestContainer("RabbitMQ"))
        {
            isAvailable = false;
            return;
        }

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

            container = new RabbitMqBuilder("rabbitmq:4.2.5-management-alpine")
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

    /// <summary>
    /// Restarts the broker container this test run started, at the same address: every client connection
    /// drops and everything non-durable on the broker is lost, as when a broker restarts in production.
    /// Inconclusive for a broker this run did not start (a configured one, or a reused container another
    /// run may be using), and when the container runtime publishes the restarted broker on another port,
    /// which no client could recover to.
    /// </summary>
    protected static async Task RestartBrokerAsync()
    {
        if (container is null)
        {
            Assert.Inconclusive("Restarting the broker needs the RabbitMQ container this test run started.");
        }

        var port = container.GetMappedPublicPort(RabbitMqBuilder.RabbitMqPort);
        await container.StopAsync();
        await container.StartAsync();

        if (container.GetMappedPublicPort(RabbitMqBuilder.RabbitMqPort) != port)
        {
            // The tests after this one connect to the new address
            ConnectionString = container.GetConnectionString();
            Assert.Inconclusive("The container runtime published the restarted broker on another port.");
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
        Action<ConduitRegistrationOverrides>? configureHandling = null,
        Action<RabbitMQOptions>? configureOptions = null)
    {
        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();

            // The self-published-event filter drops the returning broker copy before it reaches
            // the mediator, so the drop leaves no trace in SpyLog — its only signal is the
            // SelfPublishedEventSkipped observation. Registering the collector on every host lets
            // a test await that observation rather than sleep for a round trip whose duration it
            // cannot know.
            builder.Services.AddSingleton<ObservationCollector>();
            builder.Services.AddSingleton<IConduitObserver>(
                provider => provider.GetRequiredService<ObservationCollector>());

            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;

                if (topologyPrefix is not null)
                {
                    conduit.TopologyPrefix = topologyPrefix;
                }

                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(ConnectionString, configureOptions));
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
                conduit.TopologyPrefix = topologyPrefix;
                conduit.AddEndpoint(targetEndpointName, endpoint =>
                    endpoint.UseRabbitMQ(ConnectionString, configureOptions));
                conduit.Options.Routes.Add(new ConduitRoute(routeNamespace, targetEndpointName, Exact: false));
            });
        });
    }

    protected static string UniqueAppName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..15];

    /// <summary>
    /// Creates a host with the dead letter queue configured alongside RabbitMQ.
    /// Uses a unique file-based SQLite database per test so the test can query the DLQ
    /// after capture. The caller is responsible for deleting the file via cleanup.
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
                    endpoint.UseRabbitMQ(ConnectionString));

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
