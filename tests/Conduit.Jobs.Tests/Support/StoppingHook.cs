using Microsoft.Extensions.Hosting;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// A hosted service that runs a test's callback when the host stops it. Registered after
/// <c>AddConduit</c>, it is stopped before the job scheduler (the host stops services in reverse), so
/// its callback runs after <c>ApplicationStopping</c> has fired and before the scheduler's own
/// <c>StopAsync</c>: the window in which a host is stopping but its scheduler has not been told.
/// </summary>
public sealed class StoppingHook : IHostedService
{
    private readonly IServiceProvider services;
    private readonly Func<IServiceProvider, Task> onStopping;

    public StoppingHook(IServiceProvider services, Func<IServiceProvider, Task> onStopping)
    {
        this.services = services;
        this.onStopping = onStopping;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => this.onStopping(this.services);
}
