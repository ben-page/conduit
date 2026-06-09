using System.Data.Common;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Handlers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Shared registration of the audit persistence stack (dialect, connection factory, store, listener,
/// table initializer, purge service). Used by both <see cref="LocalStoreBuilder"/> and
/// <see cref="AuditConsumerBuilder"/> so every engine is wired identically.
/// </summary>
internal static class AuditStoreRegistration
{
    public static void AddSqlite(IServiceCollection services, string connectionString) =>
        Add<SqliteAuditDialect, SqliteConnection>(services, connectionString);

    public static void AddSqlServer<TConnection>(IServiceCollection services, string connectionString)
        where TConnection : DbConnection, new() =>
        Add<SqlServerAuditDialect, TConnection>(services, connectionString);

    public static void AddPostgres<TConnection>(IServiceCollection services, string connectionString)
        where TConnection : DbConnection, new() =>
        Add<PostgresAuditDialect, TConnection>(services, connectionString);

    public static void AddMySql<TConnection>(IServiceCollection services, string connectionString)
        where TConnection : DbConnection, new() =>
        Add<MySqlAuditDialect, TConnection>(services, connectionString);

    private static void Add<TDialect, TConnection>(IServiceCollection services, string connectionString)
        where TDialect : class, IAuditDialect, new()
        where TConnection : DbConnection, new()
    {
        services.AddSingleton<IAuditDialect, TDialect>();
        services.AddSingleton<IAuditConnectionFactory>(
            _ => new AuditConnectionStringFactory<TConnection>(connectionString));
        services.AddSingleton<IAuditStore, DbAuditStore>();
        services.AddTransient<IEventListener<AuditBatch>, LocalAuditListener>();
        services.AddHostedService<AuditTableInitializer>();
        services.AddHostedService<AuditPurgeService>();
    }
}
