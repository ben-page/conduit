using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.TestUtils;

/// <summary>
/// Lightweight wrapper around <see cref="WebApplication"/> for integration tests.
/// Creates a service scope so each test gets isolated scoped services,
/// and exposes <see cref="GetRequiredService{T}"/> for convenience.
/// </summary>
public sealed class TestWebHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly IServiceScope scope;

    /// <summary>
    /// The base address of the running host (e.g. "http://127.0.0.1:12345").
    /// Available after <see cref="Start"/>.
    /// </summary>
    public string Address => this.app.Urls.First();

    /// <summary>
    /// The scoped service provider for this test host.
    /// </summary>
    public IServiceProvider Services => this.scope.ServiceProvider;

    private TestWebHost(WebApplication app)
    {
        this.app = app;
        this.scope = app.Services.CreateScope();
    }

    /// <summary>
    /// Creates, configures, and starts a test host.
    /// </summary>
    /// <param name="configureBuilder">
    /// Callback to register services. This is where tests call AddConduit(),
    /// AddConduitSignalR(), and register their own types.
    /// </param>
    /// <param name="configureApp">
    /// Callback to configure middleware and endpoints (e.g. MapHub, MapConduitEndpoint).
    /// </param>
    public static TestWebHost Start(
        Action<WebApplicationBuilder> configureBuilder,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder();
        configureBuilder(builder);

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();
        return new TestWebHost(app);
    }

    /// <summary>
    /// Resolves a required service from the scoped service provider.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull
        => this.Services.GetRequiredService<T>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.scope.Dispose();
        await this.app.DisposeAsync();
    }
}
