using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Persistence;

/// <summary>
/// Base background service that runs a cleanup operation on a fixed interval. Each tick runs inside a
/// fresh DI scope (via <see cref="IServiceScopeFactory"/>) so a scoped store can be resolved safely.
/// </summary>
public abstract class PeriodicCleanupServiceBase : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>Initializes a new instance of <see cref="PeriodicCleanupServiceBase"/>.</summary>
    /// <param name="scopeFactory">Factory used to scope each cleanup tick.</param>
    protected PeriodicCleanupServiceBase(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    /// <summary>The delay between successive cleanup ticks.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Performs one cleanup pass, resolving any scoped dependencies from <paramref name="scopedProvider"/>.</summary>
    /// <param name="scopedProvider">The per-tick scoped service provider.</param>
    /// <param name="cancellationToken">A token cancelled when the host stops.</param>
    protected abstract Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using (var scope = this.scopeFactory.CreateAsyncScope())
            {
                await this.CleanupAsync(scope.ServiceProvider, stoppingToken);
            }

            await Task.Delay(this.Interval, stoppingToken);
        }
    }
}
