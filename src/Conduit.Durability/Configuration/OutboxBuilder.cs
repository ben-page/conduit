using Conduit.Durability.Dialects;
using Conduit.Durability.Store;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace Conduit.Durability.Configuration;

/// <summary>
/// Fluent builder for configuring the transactional outbox.
/// </summary>
public sealed class OutboxBuilder
{
    internal IServiceCollection Services { get; }
    internal OutboxOptions Options { get; } = new();
    internal bool StoreConfigured { get; set; }

    internal OutboxBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>Use SQLite for the outbox store.</summary>
    public OutboxBuilder UseSqlite(string connectionString)
    {
        this.Services.AddSingleton<IOutboxDialect, SqliteDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<Microsoft.Data.Sqlite.SqliteConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use SQL Server for the outbox store. Requires Microsoft.Data.SqlClient.</summary>
    public OutboxBuilder UseSqlServer<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IOutboxDialect, SqlServerDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use PostgreSQL for the outbox store. Requires Npgsql.</summary>
    public OutboxBuilder UsePostgres<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IOutboxDialect, PostgresDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use MySQL for the outbox store. Requires MySqlConnector.</summary>
    public OutboxBuilder UseMySql<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IOutboxDialect, MySqlDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Use a custom IOutboxStore implementation.</summary>
    public OutboxBuilder UseStore<TStore>() where TStore : class, IOutboxStore
    {
        this.Services.AddScoped<IOutboxStore, TStore>();
        this.StoreConfigured = true;
        return this;
    }

    /// <summary>Sets the relay polling interval.</summary>
    public OutboxBuilder WithRelayInterval(TimeSpan interval)
    {
        this.Options.RelayInterval = interval;
        return this;
    }

    /// <summary>Sets the grace period before relay claims entries.</summary>
    public OutboxBuilder WithGracePeriod(TimeSpan gracePeriod)
    {
        this.Options.GracePeriod = gracePeriod;
        return this;
    }

    /// <summary>Sets the number of entries per relay sweep.</summary>
    public OutboxBuilder WithBatchSize(int batchSize)
    {
        this.Options.BatchSize = batchSize;
        return this;
    }

    /// <summary>Sets the max retries before marking an entry as failed.</summary>
    public OutboxBuilder WithMaxRetries(int maxRetries)
    {
        this.Options.MaxRetries = maxRetries;
        return this;
    }

    /// <summary>Sets whether to auto-create the outbox table on startup.</summary>
    public OutboxBuilder WithAutoCreateTable(bool autoCreateTable)
    {
        this.Options.AutoCreateTable = autoCreateTable;
        return this;
    }
}

internal sealed class ConnectionStringFactory<TConnection>(string connectionString) : IDurabilityConnectionFactory
    where TConnection : DbConnection, new()
{
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new TConnection { ConnectionString = connectionString };
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public DbTransaction? GetCurrentTransaction() => null;
}
