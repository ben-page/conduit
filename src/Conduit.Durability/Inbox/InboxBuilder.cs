using Conduit.Durability.Configuration;
using Conduit.Durability.Inbox.Dialects;
using Conduit.Durability.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Data.Common;

namespace Conduit.Durability.Inbox;

/// <summary>
/// Fluent builder for configuring the inbox.
/// </summary>
public sealed class InboxBuilder
{
    internal IServiceCollection Services { get; }
    internal InboxOptions Options { get; } = new();
    internal bool StoreConfigured { get; set; }

    internal InboxBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>Use SQLite for the inbox store.</summary>
    public InboxBuilder UseSqlite(string connectionString)
    {
        this.Services.AddSingleton<IInboxDialect, SqliteInboxDialect>();
        this.Services.TryAddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<Microsoft.Data.Sqlite.SqliteConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use SQL Server for the inbox store. Requires Microsoft.Data.SqlClient.</summary>
    public InboxBuilder UseSqlServer<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IInboxDialect, SqlServerInboxDialect>();
        this.Services.TryAddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use PostgreSQL for the inbox store. Requires Npgsql.</summary>
    public InboxBuilder UsePostgres<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IInboxDialect, PostgresInboxDialect>();
        this.Services.TryAddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use MySQL for the inbox store. Requires MySqlConnector.</summary>
    public InboxBuilder UseMySql<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IInboxDialect, MySqlInboxDialect>();
        this.Services.TryAddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Sets the retention period for processed message IDs.</summary>
    public InboxBuilder WithRetentionPeriod(TimeSpan retentionPeriod)
    {
        this.Options.RetentionPeriod = retentionPeriod;
        return this;
    }

    /// <summary>Sets the interval between cleanup sweeps.</summary>
    public InboxBuilder WithCleanupInterval(TimeSpan cleanupInterval)
    {
        this.Options.CleanupInterval = cleanupInterval;
        return this;
    }
}
