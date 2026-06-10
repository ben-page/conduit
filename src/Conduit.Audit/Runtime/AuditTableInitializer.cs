using Conduit.Audit.Persistence;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Hosted service that creates the audit table on application startup when a local audit store is
/// configured. Extends <see cref="TableInitializerBase"/>, which scopes startup so the scoped
/// <see cref="IAuditStore"/> can be resolved safely, and fails fast when the listener is registered
/// but no persistence engine is configured.
/// </summary>
internal sealed class AuditTableInitializer : TableInitializerBase
{
    public AuditTableInitializer(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
    }

    /// <summary>
    /// Fails fast when the audit listener is registered but no persistence engine is configured
    /// (no <see cref="IConduitConnectionFactory"/>/<see cref="ISqlDialect"/> registered).
    /// </summary>
    protected override void ValidateConfiguration(IServiceProvider scopedProvider)
    {
        var connectionFactory = scopedProvider.GetService<IConduitConnectionFactory>();
        var dialect = scopedProvider.GetService<ISqlDialect>();
        if (connectionFactory is null || dialect is null)
        {
            throw new InvalidOperationException(
                "AddAuditListener was called but no persistence engine is configured. Register an " +
                "engine on the Conduit builder, for example by calling conduit.UseSqlite(connectionString) " +
                "(from the Conduit.Persistence.Sqlite package) alongside AddAuditListener().");
        }
    }

    protected override async Task CreateTablesAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IAuditStore>();
        await store.EnsureTableExistsAsync(cancellationToken);
    }
}
