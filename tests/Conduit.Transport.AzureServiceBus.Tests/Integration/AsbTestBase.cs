using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Durability;
using Conduit.Persistence.Sqlite;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.ServiceBus;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Base class for Azure Service Bus integration tests. Manages the ASB emulator container lifecycle
/// (static) and provides factory methods for creating test hosts.
/// </summary>
[TestClass]
public abstract class AsbTestBase
{
    private static ServiceBusContainer? container;
    private static bool isAvailable;

    protected static string? ConnectionString { get; private set; }
    protected static string? AdminConnectionString { get; private set; }

    public static async Task InitializeContainer()
    {
        var config = TestConfiguration.Load<AsbTestBase>();

        // If a connection string is configured, use it directly — no container needed
        var connectionString = config.GetConnectionString("AzureServiceBus");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            ConnectionString = connectionString;
            AdminConnectionString = connectionString;
            isAvailable = true;
            return;
        }

        // No connection string — start a test container if enabled
        if (!config.UseTestContainer("AzureServiceBus"))
        {
            isAvailable = false;
            return;
        }

        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=asb");

            var existing = await ContainerHelper.TryReuseContainerMultiPortAsync("conduit-test=asb", 5672, 5300);
            if (existing is not null)
            {
                var amqpPort = existing.Value.Ports[5672];
                var httpPort = existing.Value.Ports[5300];
                ConnectionString =
                    $"Endpoint=amqp://{existing.Value.Hostname}:{amqpPort}/;" +
                    "SharedAccessKeyName=RootManageSharedAccessKey;" +
                    "SharedAccessKey=SAS_KEY_VALUE;" +
                    "UseDevelopmentEmulator=true";
                AdminConnectionString =
                    $"Endpoint=sb://{existing.Value.Hostname}:{httpPort}/;" +
                    "SharedAccessKeyName=RootManageSharedAccessKey;" +
                    "SharedAccessKey=SAS_KEY_VALUE;" +
                    "UseDevelopmentEmulator=true";
                isAvailable = true;
                return;
            }

            container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
                .WithAcceptLicenseAgreement(true)
                .WithLabel("conduit-test", "asb")
                .Build();
            await container.StartAsync();
            ConnectionString = container.GetConnectionString();
            AdminConnectionString = container.GetHttpConnectionString();
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            ConnectionString = null;
            AdminConnectionString = null;
            isAvailable = false;
            Console.WriteLine($"ASB emulator not available: {exception.Message}");
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
    /// Tracks topology prefixes created by the currently-executing test.
    /// AsyncLocal flows through the test's async operations but is isolated between tests.
    /// Used by <see cref="CleanupTopologyAsync"/> to delete topics — the ASB emulator has a
    /// hard cap of 50 entities per namespace and topics won't auto-clean within a test run.
    /// Derived classes that create hosts via their own helpers should call
    /// <see cref="RegisterPrefixForCleanup"/> after host creation.
    /// </summary>
    private static readonly AsyncLocal<HashSet<string>?> CurrentTestPrefixes = new();

    /// <summary>
    /// Registers a topology prefix so its topic will be deleted in <see cref="CleanupTopologyAsync"/>.
    /// Use this from derived classes that create hosts with custom topology modes or prefixes
    /// (e.g. AsbTopologyModeShould uses Production/Developer mode prefixes that also need cleanup).
    /// </summary>
    protected static void RegisterPrefixForCleanup(string topologyPrefix) =>
        CurrentTestPrefixes.Value?.Add(topologyPrefix);

    [TestInitialize]
    public void TestInitialize()
    {
        if (!isAvailable)
        {
            Assert.Inconclusive("ASB emulator not available. Ensure Docker/Podman is running.");
        }

        CurrentTestPrefixes.Value = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Deletes all topics and queues created by the current test. The ASB emulator caps
    /// ENTITIES (queues + topics combined) at 50 and auto-delete-on-idle is 5 minutes
    /// (longer than a test run), so we must explicitly clean up every entity we create.
    /// Each Conduit host creates 1 shared topic + 1 app queue + 1 reply queue, so a test
    /// with 3 hosts leaves 7 entities behind without cleanup.
    /// </summary>
    [TestCleanup]
    public async Task CleanupTopologyAsync()
    {
        var prefixes = CurrentTestPrefixes.Value;
        CurrentTestPrefixes.Value = null;

        if (prefixes is null || prefixes.Count == 0 || AdminConnectionString is null)
        {
            return;
        }

        var adminClient = new ServiceBusAdministrationClient(AdminConnectionString);

        // Delete all topics matching any of our prefixes
        foreach (var prefix in prefixes)
        {
            var topicName = $"{prefix}-conduit";
            try
            {
                await adminClient.DeleteTopicAsync(topicName);
            }
            catch
            {
                // Best-effort — topic may not exist if host failed to start.
            }
        }

        // Delete all queues starting with any of our prefixes (app queues, reply queues, pool queues).
        // The prefix is at the start of the queue name thanks to TopologyNaming.
        try
        {
            var queuesToDelete = new List<string>();
            await foreach (var queue in adminClient.GetQueuesAsync())
            {
                foreach (var prefix in prefixes)
                {
                    if (queue.Name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        queuesToDelete.Add(queue.Name);
                        break;
                    }
                }
            }

            foreach (var queueName in queuesToDelete)
            {
                try
                {
                    await adminClient.DeleteQueueAsync(queueName);
                }
                catch
                {
                    // Best-effort — queue may have been removed concurrently.
                }
            }
        }
        catch
        {
            // Best-effort — listing queues may fail under load.
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
        var host = await TestHost.StartAsync(builder =>
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
                    endpoint.UseAzureServiceBus(ConnectionString, options =>
                        options.AdminConnectionString = AdminConnectionString));
            });

            if (configureHandling is not null)
            {
                builder.Services.ConfigureConduitRegistrations(configureHandling);
            }
        });

        if (host.TopologyPrefix is not null)
        {
            RegisterPrefixForCleanup(host.TopologyPrefix);
        }

        return host;
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
        Action<AzureServiceBusOptions>? configureOptions = null)
    {
        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.ProducerOnly = true;
                conduit.TopologyMode = TopologyMode.Test;
                conduit.TopologyPrefix = topologyPrefix;
                conduit.AddEndpoint(targetEndpointName, endpoint =>
                    endpoint.UseAzureServiceBus(ConnectionString, options =>
                    {
                        options.AdminConnectionString = AdminConnectionString;
                        configureOptions?.Invoke(options);
                    }));
                conduit.Options.Routes.Add(new ConduitRoute(routeNamespace, targetEndpointName, Exact: false));
            });
        });

        RegisterPrefixForCleanup(topologyPrefix);
        return host;
    }

    protected static string UniqueAppName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..15];

    /// <summary>
    /// Creates a host with the dead letter queue configured alongside Azure Service Bus.
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
                    endpoint.UseAzureServiceBus(ConnectionString, options =>
                        options.AdminConnectionString = AdminConnectionString));

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
