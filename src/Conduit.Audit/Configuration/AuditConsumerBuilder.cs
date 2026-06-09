using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Fluent builder for configuring the central audit consumer service.
/// The consumer receives <see cref="Runtime.AuditBatch"/> events and persists them to a local store.
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
        AuditStoreRegistration.AddSqlite(this.Services, connectionString);
        return this;
    }

    /// <summary>
    /// Use SQL Server as the audit persistence store. Requires Microsoft.Data.SqlClient.
    /// </summary>
    /// <typeparam name="TConnection">The SQL Server connection type (e.g. <c>SqlConnection</c>).</typeparam>
    /// <param name="connectionString">The SQL Server connection string.</param>
    public AuditConsumerBuilder UseSqlServer<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        AuditStoreRegistration.AddSqlServer<TConnection>(this.Services, connectionString);
        return this;
    }

    /// <summary>
    /// Use PostgreSQL as the audit persistence store. Requires Npgsql.
    /// </summary>
    /// <typeparam name="TConnection">The PostgreSQL connection type (e.g. <c>NpgsqlConnection</c>).</typeparam>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    public AuditConsumerBuilder UsePostgres<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        AuditStoreRegistration.AddPostgres<TConnection>(this.Services, connectionString);
        return this;
    }

    /// <summary>
    /// Use MySQL as the audit persistence store. Requires MySqlConnector.
    /// </summary>
    /// <typeparam name="TConnection">The MySQL connection type (e.g. <c>MySqlConnection</c>).</typeparam>
    /// <param name="connectionString">The MySQL connection string.</param>
    public AuditConsumerBuilder UseMySql<TConnection>(string connectionString)
        where TConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        AuditStoreRegistration.AddMySql<TConnection>(this.Services, connectionString);
        return this;
    }
}
