using Conduit.Audit.Persistence;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// The table initializer of a host that runs every durable feature: the outbox, the inbox, the dead-letter queue, the
/// audit trail and the jobs ledger, each store's tables created in turn as the host's initializers would.
/// </summary>
internal sealed class EveryStoreTableInitializer(
    IServiceProvider serviceProvider,
    IConduitConnectionFactory connectionFactory,
    ISqlDialect dialect)
    : TableInitializerBase(serviceProvider)
{
    protected override async Task CreateTablesAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        await new DbConnectionOutboxStore(connectionFactory, dialect).EnsureTableExistsAsync(cancellationToken);
        await new DbConnectionInboxStore(connectionFactory, dialect).EnsureTableExistsAsync(cancellationToken);
        await new DbDeadLetterStore(connectionFactory, dialect).EnsureTableExistsAsync(cancellationToken);
        await new DbAuditStore(connectionFactory, dialect).EnsureTableExistsAsync(cancellationToken);
        await new DbJobStore(connectionFactory, dialect, Options.Create(new JobsOptions())).EnsureTablesExistAsync(cancellationToken);
    }
}
