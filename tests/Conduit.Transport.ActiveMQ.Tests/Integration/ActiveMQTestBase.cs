using Amqp;
using Conduit.Configuration;
using Conduit.Durability;
using Conduit.Persistence.Sqlite;
using Conduit.Generated;
using Conduit.Runtime;
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
    private static ushort brokerPort;

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

    /// <summary>
    /// Restarts the Artemis container this test run started, dropping every connection to it and every
    /// non-durable queue on it, and returns once it accepts connections again. See <see cref="StopBrokerAsync"/>
    /// and <see cref="StartBrokerAsync"/> for when it is Inconclusive.
    /// </summary>
    protected static async Task RestartBrokerAsync()
    {
        await StopBrokerAsync();
        await StartBrokerAsync();
    }

    /// <summary>
    /// Stops the Artemis container this test run started, dropping every connection to it. Inconclusive
    /// for a configured or reused broker, which other runs may share. A test that stops the broker starts
    /// it again before it ends.
    /// </summary>
    protected static async Task StopBrokerAsync()
    {
        if (container is null)
        {
            Assert.Inconclusive("Stopping the broker needs the Artemis container this test run started.");
        }

        brokerPort = container.GetMappedPublicPort(61616);
        await container.StopAsync();
    }

    /// <summary>
    /// Starts the Artemis container <see cref="StopBrokerAsync"/> stopped and waits until it accepts
    /// connections: the startup log line the container's wait strategy looks for is already in its log
    /// from the first start, so that wait may end before this start does. Inconclusive when the container runtime
    /// publishes the restarted broker on another port (Docker may; podman keeps the port), since a
    /// transport reconnects to the address it was given.
    /// </summary>
    protected static async Task StartBrokerAsync()
    {
        if (container is null)
        {
            Assert.Inconclusive("Starting the broker needs the Artemis container this test run started.");
        }

        await container.StartAsync();

        if (container.GetMappedPublicPort(61616) != brokerPort)
        {
            // The tests after this one connect to the new address
            ConnectionString = $"amqp://admin:admin@{container.Hostname}:{container.GetMappedPublicPort(61616)}";
            Assert.Inconclusive("The container runtime published the restarted broker on another port.");
        }

        // Each attempt is bounded, so a handshake a starting broker never answers cannot hold the test
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var connection = await new ConnectionFactory()
                    .CreateAsync(new Address(ConnectionString), attempt.Token)
                    .WaitAsync(attempt.Token);
                await connection.CloseAsync();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
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
    /// The broker's connection string, for a test outside this hierarchy that needs the real broker
    /// (a topology manager test reading back what Artemis holds). Inconclusive when there is none.
    /// </summary>
    internal static string RequireBroker()
    {
        if (!isAvailable || ConnectionString is null)
        {
            Assert.Inconclusive("ActiveMQ Artemis container not available. Ensure Docker/Podman is running.");
        }

        return ConnectionString;
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
                    endpoint.UseActiveMQ(ConnectionString));
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
                    endpoint.UseActiveMQ(ConnectionString, configureOptions));
                conduit.Options.Routes.Add(new ConduitRoute(routeNamespace, targetEndpointName, Exact: false));
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
        Action<ConduitRegistrationOverrides>? configureHandling = null,
        Action<ActiveMQOptions>? configureOptions = null)
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
                    endpoint.UseActiveMQ(ConnectionString, configureOptions));

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
