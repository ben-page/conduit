using Conduit.DeadLetterQueue;
using Conduit.Durability.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Runtime.DeadLetterQueue;

/// <summary>
/// Hosted service that creates the dead letter queue table on application startup
/// when <see cref="DeadLetterQueueOptions.AutoCreateTable"/> is enabled.
/// </summary>
internal sealed class DeadLetterTableInitializer : IHostedService
{
    private readonly IServiceProvider serviceProvider;
    private readonly DeadLetterQueueOptions options;

    public DeadLetterTableInitializer(
        IServiceProvider serviceProvider,
        IOptions<DeadLetterQueueOptions> options)
    {
        this.serviceProvider = serviceProvider;
        this.options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!this.options.AutoCreateTable)
        {
            return;
        }

        await using var scope = this.serviceProvider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDeadLetterStore>();
        await store.EnsureTableExistsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
