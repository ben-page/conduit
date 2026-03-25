using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Base class for Amazon SQS integration tests. Manages the LocalStack container lifecycle (static)
/// and provides factory methods for creating test hosts.
/// </summary>
[TestClass]
public abstract class SqsTestBase
{
    private static IContainer? container;
    private static string? sharedPrefix;
    private static bool isAvailable;

    protected static string? ServiceAddress { get; private set; }
    protected static string TopologyPrefix => GetSharedPrefix();

    public static async Task InitializeContainer()
    {
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

            container = new ContainerBuilder("localstack/localstack:4.3")
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

    [TestInitialize]
    public void TestInitialize()
    {
        if (!isAvailable)
        {
            Assert.Inconclusive("LocalStack container not available. Ensure Docker/Podman is running.");
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
                    endpoint.UseAmazonSqs(options =>
                        options.ServiceUri = new Uri(ServiceAddress!)));
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
        Action<AmazonSqsOptions>? configureOptions = null)
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
                    endpoint.UseAmazonSqs(options =>
                    {
                        options.ServiceUri = new Uri(ServiceAddress!);
                        configureOptions?.Invoke(options);
                    }));
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
