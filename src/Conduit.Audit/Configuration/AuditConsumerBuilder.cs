using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Handlers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for configuring the central audit consumer service.
/// The consumer receives <see cref="AuditBatch"/> events and persists them to a local store.
/// </summary>
public sealed class AuditConsumerBuilder
{
    internal IServiceCollection Services { get; }
    internal AuditOptions Options { get; } = new();

    internal AuditConsumerBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>
    /// Use SQLite as the audit persistence store.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. "Data Source=audit.db").</param>
    public AuditConsumerBuilder UseSqlite(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        this.Services.AddSingleton<IAuditDialect, SqliteAuditDialect>();
        this.Services.AddSingleton<IAuditConnectionFactory>(
            _ => new AuditConnectionStringFactory<SqliteConnection>(connectionString));
        this.Services.AddSingleton<IAuditStore, DbAuditStore>();
        this.Services.AddTransient<IEventListener<AuditBatch>, LocalAuditListener>();
        this.Services.AddHostedService<AuditPurgeService>();
        this.Services.AddHostedService<AuditTableInitializer>();

        return this;
    }
}
