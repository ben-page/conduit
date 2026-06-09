using System.Data.Common;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Handlers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for the audit listener — the side that consumes <see cref="AuditBatch"/> events
/// and persists them. The events may originate in this process (when paired with <c>AddAudit</c>)
/// or arrive over a transport from other services (a central collector); the listener handles both
/// identically. Selects the database engine and tunes retention.
/// </summary>
public sealed class AuditListenerBuilder
{
    internal IServiceCollection Services { get; }
    internal AuditOptions Options { get; } = new();

    internal AuditListenerBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>How long stored audit records are retained before the purge removes them. Default 90 days.</summary>
    public TimeSpan RetentionPeriod
    {
        get => this.Options.RetentionPeriod;
        set => this.Options.RetentionPeriod = value;
    }

    /// <summary>How often the purge runs to remove expired records. Default 24 hours.</summary>
    public TimeSpan PurgeInterval
    {
        get => this.Options.PurgeInterval;
        set => this.Options.PurgeInterval = value;
    }

    /// <summary>
    /// Use SQLite as the audit persistence store.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string (e.g. "Data Source=audit.db").</param>
    public AuditListenerBuilder UseSqlite(string connectionString) =>
        this.Register<SqliteAuditDialect, SqliteConnection>(connectionString);

    /// <summary>
    /// Use SQL Server as the audit persistence store. Requires Microsoft.Data.SqlClient.
    /// </summary>
    /// <typeparam name="TConnection">The SQL Server connection type (e.g. <c>SqlConnection</c>).</typeparam>
    /// <param name="connectionString">The SQL Server connection string.</param>
    public AuditListenerBuilder UseSqlServer<TConnection>(string connectionString)
        where TConnection : DbConnection, new() =>
        this.Register<SqlServerAuditDialect, TConnection>(connectionString);

    /// <summary>
    /// Use PostgreSQL as the audit persistence store. Requires Npgsql.
    /// </summary>
    /// <typeparam name="TConnection">The PostgreSQL connection type (e.g. <c>NpgsqlConnection</c>).</typeparam>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    public AuditListenerBuilder UsePostgres<TConnection>(string connectionString)
        where TConnection : DbConnection, new() =>
        this.Register<PostgresAuditDialect, TConnection>(connectionString);

    /// <summary>
    /// Use MySQL as the audit persistence store. Requires MySqlConnector.
    /// </summary>
    /// <typeparam name="TConnection">The MySQL connection type (e.g. <c>MySqlConnection</c>).</typeparam>
    /// <param name="connectionString">The MySQL connection string.</param>
    public AuditListenerBuilder UseMySql<TConnection>(string connectionString)
        where TConnection : DbConnection, new() =>
        this.Register<MySqlAuditDialect, TConnection>(connectionString);

    private AuditListenerBuilder Register<TDialect, TConnection>(string connectionString)
        where TDialect : class, IAuditDialect, new()
        where TConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        this.Services.AddSingleton<IAuditDialect, TDialect>();
        this.Services.AddSingleton<IAuditConnectionFactory>(
            _ => new AuditConnectionStringFactory<TConnection>(connectionString));
        this.Services.AddSingleton<IAuditStore, DbAuditStore>();
        this.Services.AddTransient<IEventListener<AuditBatch>, LocalAuditListener>();
        this.Services.AddHostedService<AuditTableInitializer>();
        this.Services.AddHostedService<AuditPurgeService>();

        return this;
    }
}
