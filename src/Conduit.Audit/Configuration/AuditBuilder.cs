using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Handlers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for configuring the audit trail subsystem.
/// Exposes <see cref="AuditOptions"/> for tuning, plus per-type suppression methods.
/// </summary>
public sealed class AuditBuilder
{
    internal IServiceCollection Services { get; }
    internal AuditOptions Options { get; } = new();
    internal HashSet<Type> SuppressedTypes { get; } = [];
    internal HashSet<Type> SuppressedPayloadTypes { get; } = [];
    internal bool LocalStoreConfigured { get; private set; }

    internal AuditBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>
    /// Suppresses all audit recording for the specified message type.
    /// No observations will be captured or published for dispatches of <typeparamref name="T"/>.
    /// </summary>
    public AuditBuilder Suppress<T>()
    {
        this.SuppressedTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Suppresses payload capture for the specified message type.
    /// Observations are still recorded, but the message body is not included in audit records.
    /// Use for types containing sensitive data.
    /// </summary>
    public AuditBuilder SuppressPayload<T>()
    {
        this.SuppressedPayloadTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Configures a local audit store for in-process persistence of audit records.
    /// Registers the store, listener, purge service, and table initializer.
    /// </summary>
    /// <param name="configure">Callback to configure the local store (e.g. <c>UseSqlite</c>).</param>
    /// <returns>This builder for chaining.</returns>
    public AuditBuilder UseLocalStore(Action<LocalStoreBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var localStoreBuilder = new LocalStoreBuilder(this.Services);
        configure(localStoreBuilder);
        this.LocalStoreConfigured = true;

        return this;
    }
}

/// <summary>
/// Fluent builder for configuring the local audit persistence store.
/// Used within <see cref="AuditBuilder.UseLocalStore"/> to select a database engine.
/// </summary>
public sealed class LocalStoreBuilder
{
    private readonly IServiceCollection services;

    internal LocalStoreBuilder(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>
    /// Use SQLite as the local audit persistence store.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. "Data Source=audit.db").</param>
    public LocalStoreBuilder UseSqlite(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        this.services.AddSingleton<IAuditDialect, SqliteAuditDialect>();
        this.services.AddSingleton<IAuditConnectionFactory>(
            _ => new AuditConnectionStringFactory<SqliteConnection>(connectionString));
        this.services.AddSingleton<IAuditStore, DbAuditStore>();
        this.services.AddTransient<IEventListener<AuditBatch>, LocalAuditListener>();
        this.services.AddHostedService<AuditTableInitializer>();
        this.services.AddHostedService<AuditPurgeService>();

        return this;
    }
}
