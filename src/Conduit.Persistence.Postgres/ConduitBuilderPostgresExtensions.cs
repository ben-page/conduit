using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Persistence.Postgres;

/// <summary>
/// Registers the PostgreSQL persistence engine on a <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderPostgresExtensions
{
    /// <summary>
    /// Registers PostgreSQL as the persistence engine: an <see cref="IConduitConnectionFactory"/>
    /// (scoped) backed by the given connection string and an <see cref="ISqlDialect"/> (singleton).
    /// Persistence modules (durability, audit) resolve these from DI. Mirrors the transport packages'
    /// <c>UseTransport</c> pattern of reaching the service collection through the builder.
    /// </summary>
    /// <param name="builder">The Conduit builder.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <returns>The builder for chaining.</returns>
    public static ConduitBuilder UsePostgres(this ConduitBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<IConduitConnectionFactory>(
            _ => new PostgresConduitConnectionFactory(connectionString));
        builder.Services.AddSingleton<ISqlDialect, PostgresDialect>();

        return builder;
    }
}
