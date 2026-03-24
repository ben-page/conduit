using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// RabbitMQ-specific test fixture that manages a Testcontainers RabbitMQ instance
/// and configures hosts with the RabbitMQ transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Test with a shared TopologyPrefix so that all hosts within a test
/// share the same exchange/topic name. The TopologyPrefix is generated once per fixture
/// instance via ConduitOptions auto-resolve and cached for reuse across hosts.
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
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
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
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.ProducerOnly = true;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseRabbitMQ(this.connectionString!, configureOptions));
            conduit.Options.Routes.Add(new ConduitRoute
            {
                Pattern = routeNamespace,
                EndpointName = targetEndpointName,
            });
        });
    }
}
