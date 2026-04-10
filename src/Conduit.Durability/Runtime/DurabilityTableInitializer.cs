using Conduit.Durability.Configuration;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Runtime;

/// <summary>
/// Hosted service that creates all durability tables (outbox, inbox, dead letter queue)
/// on application startup. Each feature's <c>AutoCreateTable</c> option (if present) is honored.
/// Only tables for registered features are created — resolving an absent store returns null
/// and is skipped.
/// </summary>
internal sealed class DurabilityTableInitializer : IHostedService
{
    private readonly IServiceProvider serviceProvider;

    public DurabilityTableInitializer(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = this.serviceProvider.CreateAsyncScope();

        var outboxStore = scope.ServiceProvider.GetService<IOutboxStore>();
        if (outboxStore is not null)
        {
            var outboxOptions = scope.ServiceProvider.GetRequiredService<IOptions<OutboxOptions>>().Value;
            if (outboxOptions.AutoCreateTable)
            {
                await outboxStore.EnsureTableExistsAsync(cancellationToken);
            }
        }

        var inboxStore = scope.ServiceProvider.GetService<IInboxStore>();
        if (inboxStore is not null)
        {
            await inboxStore.EnsureTableExistsAsync(cancellationToken);
        }

        var deadLetterStore = scope.ServiceProvider.GetService<IDeadLetterStore>();
        if (deadLetterStore is not null)
        {
            var deadLetterOptions = scope.ServiceProvider.GetRequiredService<IOptions<DeadLetterQueueOptions>>().Value;
            if (deadLetterOptions.AutoCreateTable)
            {
                await deadLetterStore.EnsureTableExistsAsync(cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
