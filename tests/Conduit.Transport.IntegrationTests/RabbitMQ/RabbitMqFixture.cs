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
public class RabbitMqFixture : TransportTestFixture
{
    private RabbitMqContainer? container;
    private string? connectionString;

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
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseRabbitMQ(this.connectionString!));
            conduit.Route(routeNamespace).To(targetEndpointName);
        });
    }
}
