using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.TestUtils;

/// <summary>
/// Lightweight wrapper around <see cref="IHost"/> for broker transport integration tests.
/// Starts hosted services (including transport receivers) before returning.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly IHost host;
    private readonly IServiceScope scope;
    private bool disposed;

    /// <summary>
    /// The scoped service provider for this test host.
    /// </summary>
    public IServiceProvider Services => this.scope.ServiceProvider;

    /// <summary>
    /// The topology prefix this host was configured with.
    /// Used to share the same prefix across hosts within a single test.
    /// </summary>
    public string? TopologyPrefix { get; private set; }

    private TestHost(IHost host, string? topologyPrefix)
    {
        this.host = host;
        this.scope = host.Services.CreateScope();
        this.TopologyPrefix = topologyPrefix;
    }

    /// <summary>
    /// Creates, configures, and starts a test host with all hosted services.
    /// </summary>
    public static async Task<TestHost> StartAsync(
        Action<HostApplicationBuilder> configureBuilder)
    {
        var builder = Host.CreateApplicationBuilder();
        configureBuilder(builder);
        var host = builder.Build();
        try
        {
            await host.StartAsync();

            // Capture the resolved topology prefix so other hosts in the same test can share it
            var topologyPrefix = host.Services.GetService<ConduitOptions>()?.TopologyPrefix;

            return new TestHost(host, topologyPrefix);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves a required service from the scoped service provider.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull
        => this.Services.GetRequiredService<T>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.scope.Dispose();
        await this.host.StopAsync();
        this.host.Dispose();
    }
}
