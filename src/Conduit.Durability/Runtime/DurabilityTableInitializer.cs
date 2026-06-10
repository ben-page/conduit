using Conduit.Durability.Configuration;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Runtime;

/// <summary>
/// Hosted service that creates all durability tables (outbox, inbox, dead letter queue)
/// on application startup. Each feature's <c>AutoCreateTable</c> option (if present) is honored.
/// Only tables for registered features are created — resolving an absent store returns null
/// and is skipped.
/// </summary>
internal sealed class DurabilityTableInitializer : TableInitializerBase
{
    public DurabilityTableInitializer(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
    }

    protected override void ValidateConfiguration(IServiceProvider scopedProvider)
    {
        var outboxStore = scopedProvider.GetService<IOutboxStore>();
        var inboxStore = scopedProvider.GetService<IInboxStore>();
        var deadLetterStore = scopedProvider.GetService<IDeadLetterStore>();

        var anyFeatureEnabled = outboxStore is not null || inboxStore is not null || deadLetterStore is not null;
        if (anyFeatureEnabled)
        {
            ValidateEngineConfigured(scopedProvider);
        }

        if (outboxStore is not null)
        {
            ValidateOutboxConnectionFactory(scopedProvider);
        }
    }

    /// <summary>
    /// Fails fast when a durability feature is enabled but no persistence engine is configured
    /// (no <see cref="IConduitConnectionFactory"/>/<see cref="ISqlDialect"/> registered).
    /// </summary>
    private static void ValidateEngineConfigured(IServiceProvider scopedProvider)
    {
        var connectionFactory = scopedProvider.GetService<IConduitConnectionFactory>();
        var dialect = scopedProvider.GetService<ISqlDialect>();
        if (connectionFactory is null || dialect is null)
        {
            throw new InvalidOperationException(
                "Durability is enabled but no persistence engine is configured. Register an engine on " +
                "the Conduit builder, for example by calling conduit.UseSqlite(connectionString) " +
                "(from the Conduit.Persistence.Sqlite package) alongside AddDurability().");
        }
    }

    protected override async Task CreateTablesAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var outboxStore = scopedProvider.GetService<IOutboxStore>();
        if (outboxStore is not null)
        {
            var outboxOptions = scopedProvider.GetRequiredService<IOptions<OutboxOptions>>().Value;
            if (outboxOptions.AutoCreateTable)
            {
                await outboxStore.EnsureTableExistsAsync(cancellationToken);
            }
        }

        var inboxStore = scopedProvider.GetService<IInboxStore>();
        if (inboxStore is not null)
        {
            await inboxStore.EnsureTableExistsAsync(cancellationToken);
        }

        var deadLetterStore = scopedProvider.GetService<IDeadLetterStore>();
        if (deadLetterStore is not null)
        {
            var deadLetterOptions = scopedProvider.GetRequiredService<IOptions<DeadLetterQueueOptions>>().Value;
            if (deadLetterOptions.AutoCreateTable)
            {
                await deadLetterStore.EnsureTableExistsAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Fails fast when the transactional outbox is enabled but the active connection factory cannot
    /// provide the handler's ambient transaction. Without a transaction-aware factory, outbox capture
    /// silently persists nothing — there is no point at which captured messages reach the database —
    /// which is the most dangerous kind of failure: the outbox appears configured but protects nothing.
    /// </summary>
    private static void ValidateOutboxConnectionFactory(IServiceProvider scopedProvider)
    {
        var connectionFactory = scopedProvider.GetService<IConduitConnectionFactory>();
        if (connectionFactory is INonTransactionalConnectionFactory)
        {
            throw new InvalidOperationException(
                "The transactional outbox is enabled (AddOutbox), but the registered " +
                "IConduitConnectionFactory is the built-in connection-string factory, which cannot " +
                "enlist the handler's database transaction. Outbox capture would silently persist " +
                "nothing.\n\n" +
                "The transactional outbox requires a transaction-aware IConduitConnectionFactory " +
                "that returns the handler's active transaction from GetCurrentTransaction() (for " +
                "example, one backed by your DbContext or an ambient System.Transactions.Transaction). " +
                "Register it after AddConduit so it overrides the built-in factory:\n\n" +
                "    builder.Services.AddScoped<IConduitConnectionFactory, MyTransactionalConnectionFactory>();\n\n" +
                "See docs/outbox.md (\"Providing a transaction\") for a complete example. If you do not " +
                "need transactional capture, remove AddOutbox().");
        }
    }
}
