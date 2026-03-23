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
/// Uses TopologyMode.Test with a shared TestPrefix so that all hosts within a test
/// share the same SNS topic namespace. The TestPrefix is generated once per fixture
/// instance and passed to all hosts.
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

    /// <summary>
    /// The shared topology prefix used by all hosts in this fixture.
    /// </summary>
    public string TopologyPrefix { get; } = $"test-{Guid.NewGuid():N}"[..13];

    /// <summary>
    /// Starts the LocalStack container and captures the service address.
    /// Sets IsAvailable = false if the container cannot be started (e.g. Docker not running).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            this.container = new ContainerBuilder("localstack/localstack:latest")
                .WithPortBinding(4566, true)
                .WithEnvironment("SERVICES", "sns,sqs")
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
    /// Disposes all hosts and the LocalStack container.
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
        var address = this.serviceAddress!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
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
        var prefix = this.TopologyPrefix;
        var address = this.serviceAddress!;
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.TopologyMode = TopologyMode.Test;
            conduit.TestPrefix = prefix;
            conduit.AddEndpoint(targetEndpointName, endpoint =>
                endpoint.UseAmazonSqs(options =>
                {
                    options.ServiceUri = new Uri(address);
                    configureOptions?.Invoke(options);
                }));
            conduit.Route(routeNamespace).To(targetEndpointName);
        });
    }
}
