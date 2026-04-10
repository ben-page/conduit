using System.Data.Common;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Persistence.Dialects;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Runtime.Inbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Configuration;

/// <summary>
/// Fluent builder for configuring durable messaging (outbox, inbox, dead letter queue).
/// Database is configured once and shared by all features.
/// </summary>
public sealed class DurabilityBuilder
{
    internal IServiceCollection Services { get; }
    internal bool DatabaseConfigured { get; set; }

    internal DurabilityBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>Use SQLite for durability tables.</summary>
    public DurabilityBuilder UseSqlite(string connectionString)
    {
        this.Services.AddSingleton<IDurabilityDialect, SqliteDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<Microsoft.Data.Sqlite.SqliteConnection>(connectionString));
        this.DatabaseConfigured = true;
        return this;
    }

    /// <summary>Use SQL Server for durability tables. Requires Microsoft.Data.SqlClient.</summary>
    public DurabilityBuilder UseSqlServer<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IDurabilityDialect, SqlServerDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.DatabaseConfigured = true;
        return this;
    }

    /// <summary>Use PostgreSQL for durability tables. Requires Npgsql.</summary>
    public DurabilityBuilder UsePostgres<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IDurabilityDialect, PostgresDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.DatabaseConfigured = true;
        return this;
    }

    /// <summary>Use MySQL for durability tables. Requires MySqlConnector.</summary>
    public DurabilityBuilder UseMySql<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        this.Services.AddSingleton<IDurabilityDialect, MySqlDialect>();
        this.Services.AddScoped<IDurabilityConnectionFactory>(
            _ => new ConnectionStringFactory<TConnection>(connectionString));
        this.DatabaseConfigured = true;
        return this;
    }

    /// <summary>Adds the transactional outbox.</summary>
    public DurabilityBuilder AddOutbox(Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IOutboxStore, DbConnectionOutboxStore>();

        this.Services.AddScoped<OutboxCapture>();
        this.Services.AddScoped<IOutboundInterceptor>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());
        this.Services.AddScoped<IOutboxFlusher>(
            serviceProvider => serviceProvider.GetRequiredService<OutboxCapture>());

        this.Services.TryAddTransient(typeof(OutboxBehavior<,>));

        this.Services.AddHostedService<OutboxRelay>();

        this.Services.AddSingleton(Options.Create(options));
        return this;
    }

    /// <summary>Adds the inbox (idempotent message processing).</summary>
    public DurabilityBuilder AddInbox(Action<InboxOptions>? configure = null)
    {
        var options = new InboxOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IInboxStore, DbConnectionInboxStore>();
        this.Services.AddScoped<IInboundInterceptor, InboxInterceptor>();
        this.Services.AddHostedService<InboxCleanup>();
        this.Services.AddSingleton(Options.Create(options));

        return this;
    }

    /// <summary>Adds the dead letter queue (capture failed messages, browse, replay).</summary>
    public DurabilityBuilder AddDeadLetterQueue(Action<DeadLetterQueueOptions>? configure = null)
    {
        var options = new DeadLetterQueueOptions();
        configure?.Invoke(options);

        this.Services.TryAddScoped<IDeadLetterStore, DbDeadLetterStore>();
        this.Services.TryAddScoped<IDeadLetters, Runtime.DeadLetterQueue.DeadLetters>();
        this.Services.AddScoped<IInboundErrorInterceptor, DeadLetterErrorInterceptor>();
        this.Services.AddHostedService<DeadLetterTableInitializer>();
        this.Services.AddSingleton(Options.Create(options));

        return this;
    }
}
