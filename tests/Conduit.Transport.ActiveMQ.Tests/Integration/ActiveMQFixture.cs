using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Transport.ActiveMQ;
using Conduit.TestUtils;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// ActiveMQ Artemis-specific test fixture that manages a Testcontainers Artemis instance
/// and configures hosts with the ActiveMQ transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Test with a shared TestPrefix so that all hosts within a test
/// share the same address/queue namespace. The TestPrefix is generated once per fixture
/// instance and passed to all hosts.
/// </remarks>
public class ActiveMQFixture : TransportTestFixture
{
    private IContainer? container;
    private string? connectionString;

    /// <summary>
    /// The AMQP 1.0 connection string for the running container.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string ConnectionString => this.connectionString!;

    /// <summary>
    /// The shared topology prefix used by all hosts in this fixture.
    /// </summary>
    public string TopologyPrefix { get; } = $"test-{Guid.NewGuid():N}"[..13];

    /// <summary>
    /// Starts the ActiveMQ Artemis container and captures the connection string.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            this.container = new ContainerBuilder("apache/activemq-artemis:2.37.0-alpine")
                .WithPortBinding(61616, true)
                .WithEnvironment("ARTEMIS_USER", "admin")
                .WithEnvironment("ARTEMIS_PASSWORD", "admin")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilMessageIsLogged("AMQ241004"))
                .Build();

            await this.container.StartAsync();

            var mappedPort = this.container.GetMappedPublicPort(61616);
            this.connectionString = $"amqp://admin:admin@{this.container.Hostname}:{mappedPort}";
            this.IsAvailable = true;
        }
        catch (Exception exception)
        {
            this.container = null;
            this.connectionString = null;
            this.IsAvailable = false;
            Console.WriteLine($"ActiveMQ Artemis container not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Disposes all hosts and the Artemis container.
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
        var connString = this.connectionString!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
            conduit.AddEndpoint("self", endpoint =>
                endpoint.UseActiveMQ(connString));
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
    /// Configures a sender host with optional ActiveMQ options override (e.g., short ResponseTimeout).
    /// </summary>
    public void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName,
        Action<ActiveMQOptions>? configureOptions)
    {
        var prefix = this.TopologyPrefix;
        var connString = this.connectionString!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.ProducerOnly = true;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseActiveMQ(connString, configureOptions));
            conduit.Route(routeNamespace).To(targetEndpointName);
        });
    }
}
