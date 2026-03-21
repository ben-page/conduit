using Conduit.Configuration;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.IntegrationTests.Support;

/// <summary>
/// Abstract base class for multi-host broker transport integration tests.
/// Manages TestHost lifecycle and provides factory methods for creating
/// handler hosts and sender hosts with routing.
/// </summary>
public abstract class TransportTestFixture
{
    private readonly List<TestHost> hosts = [];

    /// <summary>
    /// Whether the underlying broker is available.
    /// Tests should check this and call Assert.Inconclusive when false.
    /// </summary>
    public bool IsAvailable { get; protected set; }

    /// <summary>
    /// Creates a handler/listener host connected to the broker.
    /// The host starts all hosted services (including transport receivers) before returning.
    /// </summary>
    /// <param name="applicationName">Unique application name for queue/topic naming.</param>
    /// <returns>A started TestHost with the transport configured.</returns>
    public async Task<TestHost> CreateHostAsync(string applicationName)
    {
        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            this.ConfigureTransport(builder, applicationName);
        });

        this.hosts.Add(host);
        return host;
    }

    /// <summary>
    /// Creates a sender host that routes a namespace to a target endpoint.
    /// The host starts all hosted services before returning.
    /// </summary>
    /// <param name="applicationName">Unique application name for this sender.</param>
    /// <param name="routeNamespace">Namespace pattern to route (e.g. "MyApp.Commands").</param>
    /// <param name="targetEndpointName">Name of the endpoint to route to.</param>
    /// <returns>A started TestHost with transport and routing configured.</returns>
    public async Task<TestHost> CreateSenderHostAsync(
        string applicationName,
        string routeNamespace,
        string targetEndpointName)
    {
        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            this.ConfigureSenderTransport(builder, applicationName, routeNamespace, targetEndpointName);
        });

        this.hosts.Add(host);
        return host;
    }

    /// <summary>
    /// Configures a handler/listener host with the broker transport.
    /// Implementations should call builder.Services.AddConduit() with the transport
    /// and set conduit.ApplicationName.
    /// </summary>
    protected abstract void ConfigureTransport(
        HostApplicationBuilder builder,
        string applicationName);

    /// <summary>
    /// Configures a sender host with the broker transport and routing.
    /// Implementations should call builder.Services.AddConduit() with the transport,
    /// set conduit.ApplicationName, and add conduit.Route(routeNamespace).To(targetEndpointName).
    /// </summary>
    protected abstract void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName);

    /// <summary>
    /// Disposes all hosts created during the test. Call from [TestCleanup].
    /// </summary>
    public async Task DisposeHostsAsync()
    {
        foreach (var host in this.hosts)
        {
            await host.DisposeAsync();
        }

        this.hosts.Clear();
    }

    /// <summary>
    /// Generates a short unique application name with the given prefix.
    /// Suitable for broker resource naming (kept short to avoid length limits).
    /// </summary>
    public static string UniqueAppName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..15];
}
