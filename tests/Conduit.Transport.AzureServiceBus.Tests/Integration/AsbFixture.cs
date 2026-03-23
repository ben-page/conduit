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
/// Uses TopologyMode.Test with a shared TestPrefix so that all hosts within a test
/// share the same topic/subscription namespace. The TestPrefix is generated once per
/// fixture instance and passed to all hosts.
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

    /// <summary>
    /// The shared topology prefix used by all hosts in this fixture.
    /// </summary>
    public string TopologyPrefix { get; } = $"test-{Guid.NewGuid():N}"[..13];

    /// <summary>
    /// Starts the ASB emulator container and captures connection strings.
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
            this.container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
                .WithAcceptLicenseAgreement(true)
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
    /// Disposes all hosts and the ASB emulator container.
    /// </summary>
    public async Task CleanupAsync()
    {
        await this.DisposeHostsAsync();

        if (this.container is not null)
        {
            await this.container.DisposeAsync();
        }
    }

    /// <inheritdoc />
    protected override void ConfigureTransport(
        HostApplicationBuilder builder,
        string applicationName)
    {
        var prefix = this.TopologyPrefix;
        var connStr = this.connectionString!;
        var adminConnStr = this.adminConnectionString;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
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
        var prefix = this.TopologyPrefix;
        var connStr = this.connectionString!;
        var adminConnStr = this.adminConnectionString;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseAzureServiceBus(connStr, options =>
                    options.AdminConnectionString = adminConnStr));
            conduit.Route(routeNamespace).To(targetEndpointName);
        });
    }
}
