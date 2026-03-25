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
/// Amazon SQS-specific test fixture that manages a Testcontainers LocalStack instance
/// and configures hosts with the SQS (+SNS) transport.
/// </summary>
/// <remarks>
/// Uses TopologyMode.Test with a shared TopologyPrefix so that all hosts within a test
/// share the same SNS topic namespace. The TopologyPrefix is generated once per fixture
/// instance via ConduitOptions auto-resolve and cached for reuse across hosts.
/// </remarks>
public class SqsFixture : TransportTestFixture
{
    private IContainer? container;
    private string? serviceAddress;

    /// <summary>
    /// The LocalStack service address for the running container.
    /// Only valid when <see cref="TransportTestFixture.IsAvailable"/> is true.
    /// </summary>
    public string ServiceAddress => this.serviceAddress!;

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
    /// Starts the LocalStack container and captures the service address.
    /// Cleans up orphaned containers from previous runs and supports reuse
    /// via the CONDUIT_REUSE_CONTAINERS environment variable.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await CleanupOrphanedContainersAsync("conduit-test=sqs");

            var existing = await TryReuseContainerAsync("conduit-test=sqs", 4566);
            if (existing is not null)
            {
                this.serviceAddress = $"http://{existing.Value.Hostname}:{existing.Value.Port}";
                this.IsAvailable = true;
                return;
            }

            this.container = new ContainerBuilder("localstack/localstack:4.3")
                .WithPortBinding(4566, true)
                .WithEnvironment("SERVICES", "sns,sqs")
                .WithLabel("conduit-test", "sqs")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(request =>
                        request.ForPath("/_localstack/health").ForPort(4566)))
                .Build();

            await this.container.StartAsync();
            var mappedPort = this.container.GetMappedPublicPort(4566);
            this.serviceAddress = $"http://{this.container.Hostname}:{mappedPort}";
            this.IsAvailable = true;
        }
        catch (Exception exception)
        {
            this.container = null;
            this.serviceAddress = null;
            this.IsAvailable = false;
            Console.WriteLine($"LocalStack container not available: {exception.Message}");
        }
    }

    /// <summary>
    /// Disposes all hosts and the LocalStack container (unless reuse is enabled).
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
        var address = this.serviceAddress!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
            conduit.AddEndpoint("self", endpoint =>
                endpoint.UseAmazonSqs(options =>
                    options.ServiceUri = new Uri(address)));
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
    /// Configures a sender host with optional SQS options override (e.g., short ResponseTimeout).
    /// </summary>
    public void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName,
        Action<AmazonSqsOptions>? configureOptions)
    {
        var address = this.serviceAddress!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.ProducerOnly = true;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TopologyPrefix = this.GetSharedPrefix();
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseAmazonSqs(options =>
                {
                    options.ServiceUri = new Uri(address);
                    configureOptions?.Invoke(options);
                }));
            conduit.Options.Routes.Add(new ConduitRoute
            {
                Pattern = routeNamespace,
                EndpointName = targetEndpointName,
            });
        });
    }
}
