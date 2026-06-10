using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Persistence.SqlServer;

/// <summary>
/// Registers the SQL Server persistence engine on a <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderSqlServerExtensions
{
    /// <summary>
    /// Registers SQL Server as the persistence engine: an <see cref="IConduitConnectionFactory"/>
    /// (scoped) backed by the given connection string and an <see cref="ISqlDialect"/> (singleton).
    /// Persistence modules (durability, audit) resolve these from DI. Mirrors the transport packages'
    /// <c>UseTransport</c> pattern of reaching the service collection through the builder.
    /// </summary>
    /// <param name="builder">The Conduit builder.</param>
    /// <param name="connectionString">The SQL Server connection string.</param>
    /// <returns>The builder for chaining.</returns>
    public static ConduitBuilder UseSqlServer(this ConduitBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<IConduitConnectionFactory>(
            _ => new SqlServerConduitConnectionFactory(connectionString));
        builder.Services.AddSingleton<ISqlDialect, SqlServerDialect>();

        return builder;
    }
}
