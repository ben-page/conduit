using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Persistence;

/// <summary>
/// Base hosted service that creates persistence tables on application startup. Subclasses override
/// <see cref="ValidateConfiguration"/> to fail fast on misconfiguration (e.g. the outbox requiring a
/// transaction-aware connection factory) and <see cref="CreateTablesAsync"/> to iterate the module's
/// registered stores and create their tables.
/// </summary>
public abstract class TableInitializerBase : IHostedService
{
    private readonly IServiceProvider serviceProvider;

    /// <summary>Initializes a new instance of <see cref="TableInitializerBase"/>.</summary>
    /// <param name="serviceProvider">The root service provider; a scope is created per startup.</param>
    protected TableInitializerBase(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = this.serviceProvider.CreateAsyncScope();

        this.ValidateConfiguration(scope.ServiceProvider);
        await this.CreateTablesAsync(scope.ServiceProvider, cancellationToken);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Validates the resolved configuration before any table is created. The default is a no-op;
    /// override to throw an actionable exception when the configuration is guaranteed-broken.
    /// </summary>
    /// <param name="scopedProvider">The scoped service provider for this startup.</param>
    protected virtual void ValidateConfiguration(IServiceProvider scopedProvider)
    {
    }

    /// <summary>
    /// Creates the module's tables for every registered store. Stores that are not registered should
    /// be skipped (resolving an absent store returns <see langword="null"/>).
    /// </summary>
    /// <param name="scopedProvider">The scoped service provider for this startup.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    protected abstract Task CreateTablesAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken);
}
