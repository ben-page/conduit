using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Kafka;

namespace Conduit.Transport.Kafka.Tests.Integration;

/// <summary>
/// Kafka-specific test fixture that manages a Testcontainers Kafka instance (KRaft mode)
/// and configures hosts with the Kafka transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Test with a shared TopologyPrefix so that all hosts within a test
/// share the same topic namespace. The TopologyPrefix is generated once per fixture
/// instance via ConduitOptions auto-resolve and cached for reuse across hosts.
/// </remarks>
public class KafkaFixture : TransportTestFixture
{
    private KafkaContainer? container;
    private string? bootstrapServers;

    /// <summary>
    /// The Kafka bootstrap servers string for the running container.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string BootstrapServers => this.bootstrapServers!;

    private string? sharedPrefix;

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
    /// Starts the Kafka container (KRaft mode, no ZooKeeper) and captures the bootstrap servers.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            // Use Testcontainers.Kafka for proper advertised listener handling
            this.container = new KafkaBuilder("confluentinc/cp-kafka:7.7.1")
                .Build();

            await this.container.StartAsync();
            this.bootstrapServers = this.container.GetBootstrapAddress();
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
        var servers = this.bootstrapServers!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
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
        var servers = this.bootstrapServers!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.ProducerOnly = true;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseKafka(servers, configureOptions));
            conduit.Options.Routes.Add(new ConduitRoute
            {
                Pattern = routeNamespace,
                EndpointName = targetEndpointName,
            });
        });
    }
}
