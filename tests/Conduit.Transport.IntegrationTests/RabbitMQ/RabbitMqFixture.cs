using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Transport.IntegrationTests.Support;
using Conduit.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.IntegrationTests.RabbitMQ;

/// <summary>
/// RabbitMQ-specific test fixture that manages a Testcontainers RabbitMQ instance
/// and configures hosts with the RabbitMQ transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Developer with a shared prefix so that all hosts within a test
/// share the same exchange/topic name. The Testcontainers instance provides broker-level
/// isolation, so a random-per-host prefix (TopologyMode.Test) would prevent hosts from
/// communicating.
/// </remarks>
public class RabbitMqFixture : TransportTestFixture
{
    private RabbitMqContainer? container;
    private string? connectionString;
    /// <summary>
    /// The AMQP connection string for the running container.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string ConnectionString => this.connectionString!;

    /// <summary>
    /// The shared topology prefix used by all hosts in this fixture.
    /// </summary>
    public string TopologyPrefix { get; } = $"test-{Guid.NewGuid():N}"[..13];

    /// <summary>
    /// Starts the RabbitMQ container and captures the connection string.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            this.container = new RabbitMqBuilder("rabbitmq:4-management-alpine")
                .Build();
            await this.container.StartAsync();
            this.connectionString = this.container.GetConnectionString();
            this.IsAvailable = true;
        }
        catch (Exception exception)
        {
            this.container = null;
            this.connectionString = null;
            this.IsAvailable = false;
            Console.WriteLine($"RabbitMQ container not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Disposes all hosts and the RabbitMQ container.
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
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Developer;
            conduit.DeveloperPrefix = prefix;
            conduit.AddEndpoint("self", endpoint =>
                endpoint.UseRabbitMQ(this.connectionString!));
        });
    }

    /// <inheritdoc />
    protected override void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName)
    {
        this.ConfigureSenderTransport(builder, applicationName, routeNamespace, targetEndpointName, null);
    }

    /// <summary>
    /// Configures a sender host with optional RabbitMQ options override (e.g., short ResponseTimeout).
    /// </summary>
    public void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName,
        Action<RabbitMQOptions>? configureOptions)
    {
        var prefix = this.TopologyPrefix;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Developer;
            conduit.DeveloperPrefix = prefix;
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseRabbitMQ(this.connectionString!, configureOptions));
            conduit.Route(routeNamespace).To(targetEndpointName);
        });
    }
}
