using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Transport.AzureServiceBus;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.ServiceBus;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Azure Service Bus-specific test fixture that manages a Testcontainers ASB emulator instance
/// and configures hosts with the Azure Service Bus transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Test with a shared TopologyPrefix so that all hosts within a test
/// share the same topic/subscription namespace. The TopologyPrefix is generated once per
/// fixture instance via ConduitOptions auto-resolve and cached for reuse across hosts.
/// </remarks>
public class AsbFixture : TransportTestFixture
{
    private ServiceBusContainer? container;
    private string? connectionString;
    private string? adminConnectionString;

    /// <summary>
    /// The AMQP connection string for the running container.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string ConnectionString => this.connectionString!;

    /// <summary>
    /// The HTTP admin connection string for topology management.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string AdminConnectionString => this.adminConnectionString!;

    private string? sharedPrefix;

    /// <summary>
    /// The shared topology prefix for test isolation.
    /// </summary>
    public string TopologyPrefix => this.GetSharedPrefix();

    private string GetSharedPrefix()
    {
        if (this.sharedPrefix is null)
        {
            var seed = new ConduitOptions { TopologyMode = TopologyMode.Test };
            this.sharedPrefix = seed.TopologyPrefix!;
        }

        return this.sharedPrefix;
    }

    /// <summary>
    /// Starts the ASB emulator container and captures connection strings.
    /// Cleans up orphaned containers from previous runs and supports reuse
    /// via the CONDUIT_REUSE_CONTAINERS environment variable.
    /// Falls back to CONDUIT_ASB_CONNECTION_STRING environment variable if set.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        var envConnectionString = Environment.GetEnvironmentVariable("CONDUIT_ASB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envConnectionString))
        {
            this.connectionString = envConnectionString;
            this.adminConnectionString = envConnectionString;
            this.IsAvailable = true;
            return;
        }

        try
        {
            await CleanupOrphanedContainersAsync("conduit-test=asb");

            var existing = await TryReuseContainerMultiPortAsync("conduit-test=asb", 5672, 8080);
            if (existing is not null)
            {
                var amqpPort = existing.Value.Ports[5672];
                var httpPort = existing.Value.Ports[8080];
                this.connectionString =
                    $"Endpoint=sb://{existing.Value.Hostname}:{amqpPort};" +
                    "SharedAccessKeyName=RootManageSharedAccessKey;" +
                    "SharedAccessKey=SAS_KEY_VALUE;" +
                    "UseDevelopmentEmulator=true";
                this.adminConnectionString =
                    $"Endpoint=http://{existing.Value.Hostname}:{httpPort};" +
                    "SharedAccessKeyName=RootManageSharedAccessKey;" +
                    "SharedAccessKey=SAS_KEY_VALUE;" +
                    "UseDevelopmentEmulator=true";
                this.IsAvailable = true;
                return;
            }

            this.container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
                .WithAcceptLicenseAgreement(true)
                .WithLabel("conduit-test", "asb")
                .Build();
            await this.container.StartAsync();
            this.connectionString = this.container.GetConnectionString();
            this.adminConnectionString = this.container.GetHttpConnectionString();
            this.IsAvailable = true;
        }
        catch (Exception exception)
        {
            this.container = null;
            this.connectionString = null;
            this.adminConnectionString = null;
            this.IsAvailable = false;
            Console.WriteLine($"ASB emulator not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Disposes all hosts and the ASB emulator container (unless reuse is enabled).
    /// </summary>
    public async Task CleanupAsync()
    {
        await this.DisposeHostsAsync();

        if (this.container is not null && !ReuseContainers)
        {
            await this.container.DisposeAsync();
        }
    }

    /// <inheritdoc />
    protected override void ConfigureTransport(
        HostApplicationBuilder builder,
        string applicationName)
    {
        var connStr = this.connectionString!;
        var adminConnStr = this.adminConnectionString;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
            conduit.AddEndpoint("self", endpoint =>
                endpoint.UseAzureServiceBus(connStr, options =>
                    options.AdminConnectionString = adminConnStr));
        });
    }

    /// <inheritdoc />
    protected override void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName)
    {
        var connStr = this.connectionString!;
        var adminConnStr = this.adminConnectionString;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.ProducerOnly = true;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseAzureServiceBus(connStr, options =>
                    options.AdminConnectionString = adminConnStr));
            conduit.Options.Routes.Add(new ConduitRoute
            {
                Pattern = routeNamespace,
                EndpointName = targetEndpointName,
            });
        });
    }
}
