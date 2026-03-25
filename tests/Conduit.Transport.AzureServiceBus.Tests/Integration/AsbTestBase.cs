using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus;
using Microsoft.Extensions.Configuration;
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
    private static string? sharedPrefix;
    private static bool isAvailable;

    protected static string? ConnectionString { get; private set; }
    protected static string? AdminConnectionString { get; private set; }
    protected static string TopologyPrefix => GetSharedPrefix();

    public static async Task InitializeContainer()
    {
        // Priority: 1) user secrets, 2) env var, 3) emulator container
        var connectionString = LoadConnectionStringFromSecrets()
            ?? Environment.GetEnvironmentVariable("CONDUIT_ASB_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            ConnectionString = connectionString;
            AdminConnectionString = connectionString;
            isAvailable = true;
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
                // AMQP connection string uses amqp:// scheme
                ConnectionString =
                    $"Endpoint=amqp://{existing.Value.Hostname}:{amqpPort}/;" +
                    "SharedAccessKeyName=RootManageSharedAccessKey;" +
                    "SharedAccessKey=SAS_KEY_VALUE;" +
                    "UseDevelopmentEmulator=true";
                // Admin connection string uses sb:// scheme on the HTTP port
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

    [TestInitialize]
    public void TestInitialize()
    {
        if (!isAvailable)
        {
            Assert.Inconclusive("ASB emulator not available. Ensure Docker/Podman is running.");
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
                    endpoint.UseAzureServiceBus(ConnectionString!, options =>
                        options.AdminConnectionString = AdminConnectionString));
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
        Action<AzureServiceBusOptions>? configureOptions = null)
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
                    endpoint.UseAzureServiceBus(ConnectionString!, options =>
                    {
                        options.AdminConnectionString = AdminConnectionString;
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

    private static string? LoadConnectionStringFromSecrets()
    {
        try
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets<AsbTestBase>()
                .Build();

            return config["Conduit:AzureServiceBus:ConnectionString"];
        }
        catch
        {
            return null;
        }
    }
}
