using Conduit.Configuration;
using Conduit.Durability;
using Conduit.Persistence.Sqlite;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Base class for Amazon SQS integration tests. Manages the LocalStack container lifecycle (static)
/// and provides factory methods for creating test hosts with per-test topology isolation.
/// </summary>
[TestClass]
public abstract class SqsTestBase
{
    private static IContainer? container;
    private static bool isAvailable;

    protected static string? ServiceAddress { get; private set; }

    public static async Task InitializeContainer()
    {
        var config = TestConfiguration.Load<SqsTestBase>();

        var serviceUri = config.Get("AmazonSqs", "ServiceUri");
        if (!string.IsNullOrWhiteSpace(serviceUri))
        {
            ServiceAddress = serviceUri;
            isAvailable = true;
            return;
        }

        if (!config.UseTestContainer("AmazonSqs"))
        {
            isAvailable = false;
            return;
        }

        try
        {
            await ContainerHelper.CleanupOrphanedContainersAsync("conduit-test=sqs");

            var existing = await ContainerHelper.TryReuseContainerAsync("conduit-test=sqs", 4566);
            if (existing is not null)
            {
                ServiceAddress = $"http://{existing.Value.Hostname}:{existing.Value.Port}";
                isAvailable = true;
                return;
            }

            container = new ContainerBuilder("localstack/localstack:4.14.0")
                .WithPortBinding(4566, true)
                .WithEnvironment("SERVICES", "sns,sqs")
                .WithLabel("conduit-test", "sqs")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request =>
                        request.ForPath("/_localstack/health").ForPort(4566)))
                .Build();

            await container.StartAsync();
            ServiceAddress = $"http://{container.Hostname}:{container.GetMappedPublicPort(4566)}";
            isAvailable = true;
        }
        catch (Exception exception)
        {
            container = null;
            ServiceAddress = null;
            isAvailable = false;
            Console.WriteLine($"LocalStack container not available: {exception.Message}");
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
    /// Reaps SQS/SNS resources created by THIS test project in prior runs (everything under the
    /// project's stable <c>test-{hash}-</c> prefix). Called once per project from AssemblySetup.
    /// </summary>
    internal static async Task ReapProjectResourcesAsync()
    {
        if (!isAvailable || ServiceAddress is null)
        {
            return;
        }

        var options = new AmazonSqsOptions { ServiceUri = new Uri(ServiceAddress) };
        await AmazonSqsTopologyReaper.ReapByPrefixAsync(
            options,
            TestTopologyPrefix.ProjectPrefix(typeof(SqsTestBase).Assembly));
    }

    [TestInitialize]
    public void TestInitialize()
    {
        if (!isAvailable)
        {
            Assert.Inconclusive("LocalStack container not available. Ensure Docker/Podman is running.");
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
        var address = ServiceAddress;

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
                conduit.TopologyPrefix = topologyPrefix
                    ?? TestTopologyPrefix.NewForTest(typeof(SqsTestBase).Assembly);

                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAmazonSqs(options =>
                        options.ServiceUri = new Uri(address!)));
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
        Action<AmazonSqsOptions>? configureOptions = null)
    {
        var address = ServiceAddress;

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
                    endpoint.UseAmazonSqs(options =>
                    {
                        options.ServiceUri = new Uri(address!);
                        configureOptions?.Invoke(options);
                    }));
                conduit.Options.Routes.Add(new ConduitRoute(routeNamespace, targetEndpointName, Exact: false));
            });
        });
    }

    protected static string UniqueAppName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..15];

    /// <summary>
    /// Creates a host with the dead letter queue configured alongside SQS.
    /// </summary>
    protected static async Task<TestHost> CreateHostWithDeadLettersAsync(
        string applicationName,
        string dlqDatabasePath,
        string? topologyPrefix = null,
        Action<ConduitRegistrationOverrides>? configureHandling = null)
    {
        var address = ServiceAddress;
        var connectionString = $"Data Source={dlqDatabasePath}";

        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Test;
                conduit.TopologyPrefix = topologyPrefix
                    ?? TestTopologyPrefix.NewForTest(typeof(SqsTestBase).Assembly);

                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAmazonSqs(options =>
                        options.ServiceUri = new Uri(address!)));

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
