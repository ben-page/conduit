using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.Kafka;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Kafka.Tests.Integration;

/// <summary>
/// Kafka-specific test fixture that manages a Testcontainers Kafka instance (KRaft mode)
/// and configures hosts with the Kafka transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Test with a shared TestPrefix so that all hosts within a test
/// share the same topic namespace. The TestPrefix is generated once per fixture
/// instance and passed to all hosts.
/// </remarks>
public class KafkaFixture : TransportTestFixture
{
    private IContainer? container;
    private string? bootstrapServers;

    /// <summary>
    /// The Kafka bootstrap servers string for the running container.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string BootstrapServers => this.bootstrapServers!;

    /// <summary>
    /// The shared topology prefix used by all hosts in this fixture.
    /// </summary>
    public string TopologyPrefix { get; } = $"test-{Guid.NewGuid():N}"[..13];

    /// <summary>
    /// Starts the Kafka container (KRaft mode, no ZooKeeper) and captures the bootstrap servers.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            this.container = new ContainerBuilder("confluentinc/cp-kafka:7.7.1")
                .WithPortBinding(9092, true)
                .WithEnvironment("KAFKA_NODE_ID", "1")
                .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
                .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
                .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://0.0.0.0:29092,CONTROLLER://0.0.0.0:9093,EXTERNAL://0.0.0.0:9092")
                .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", "PLAINTEXT://localhost:29092,EXTERNAL://0.0.0.0:9092")
                .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "PLAINTEXT:PLAINTEXT,CONTROLLER:PLAINTEXT,EXTERNAL:PLAINTEXT")
                .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
                .WithEnvironment("KAFKA_INTER_BROKER_LISTENER_NAME", "PLAINTEXT")
                .WithEnvironment("CLUSTER_ID", "MkU3OEVBNTcwNTJENDM2Qk")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started"))
                .Build();

            await this.container.StartAsync();

            var mappedPort = this.container.GetMappedPublicPort(9092);
            this.bootstrapServers = $"{this.container.Hostname}:{mappedPort}";
            this.IsAvailable = true;
        }
        catch (Exception exception)
        {
            this.container = null;
            this.bootstrapServers = null;
            this.IsAvailable = false;
            Console.WriteLine($"Kafka container not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Disposes all hosts and the Kafka container.
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
        var servers = this.bootstrapServers!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
            conduit.AddEndpoint("self", endpoint =>
                endpoint.UseKafka(servers));
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
    /// Configures a sender host with optional Kafka options override (e.g., short ResponseTimeout).
    /// </summary>
    public void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName,
        Action<KafkaOptions>? configureOptions)
    {
        var prefix = this.TopologyPrefix;
        var servers = this.bootstrapServers!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseKafka(servers, configureOptions));
            conduit.Route(routeNamespace).To(targetEndpointName);
        });
    }
}
