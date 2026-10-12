using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Persistence;

/// <summary>
/// Base background service that runs a cleanup operation on a fixed interval. Each tick runs inside a
/// fresh DI scope (via <see cref="IServiceScopeFactory"/>) so a scoped store can be resolved safely.
/// </summary>
/// <remarks>
/// A tick that throws — a purge chosen as a deadlock victim, a dropped connection — is logged, and the
/// next tick comes round after <see cref="Interval"/> as usual. Escaping, it would end the service, and
/// a host whose <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c> (the default) would stop
/// with it. A tick that throws while the host is stopping ends the service quietly, whatever the
/// exception: that is the stop's cancellation. A failure is logged under the derived type's category
/// through the host's <see cref="ILoggerFactory"/>, resolved from the first tick's scope, so a derived
/// class's constructor needs no logger.
/// </remarks>
public abstract partial class PeriodicCleanupServiceBase : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private ILogger? logger;

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
            try
            {
                await using var scope = this.scopeFactory.CreateAsyncScope();

                // A singleton, so holding it past this scope is safe.
                this.logger ??= scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(this.GetType());
                await this.CleanupAsync(scope.ServiceProvider, stoppingToken);
            }
#pragma warning disable CA1031 // While stopping, any exception is the stop's cancellation: SqlClient reports it as a SqlException.
            catch (Exception) when (stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                return;
            }
#pragma warning disable CA1031 // One failed tick must not stop the host; the next tick tries again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogCleanupFailed(this.logger ?? NullLogger.Instance, this.GetType().Name, this.Interval, exception);
            }

            await Task.Delay(this.Interval, stoppingToken);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "{CleanupService} cleanup pass failed; the next pass runs in {Interval}")]
    private static partial void LogCleanupFailed(
        ILogger logger, string cleanupService, TimeSpan interval, Exception exception);
}
