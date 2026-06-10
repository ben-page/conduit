using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Persistence.MySql;

/// <summary>
/// Registers the MySQL persistence engine on a <see cref="ConduitBuilder"/>.
/// </summary>
public static class ConduitBuilderMySqlExtensions
{
    /// <summary>
    /// Registers MySQL as the persistence engine: an <see cref="IConduitConnectionFactory"/>
    /// (scoped) backed by the given connection string and an <see cref="ISqlDialect"/> (singleton).
    /// Persistence modules (durability, audit) resolve these from DI. Mirrors the transport packages'
    /// <c>UseTransport</c> pattern of reaching the service collection through the builder.
    /// </summary>
    /// <param name="builder">The Conduit builder.</param>
    /// <param name="connectionString">The MySQL connection string.</param>
    /// <returns>The builder for chaining.</returns>
    public static ConduitBuilder UseMySql(this ConduitBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<IConduitConnectionFactory>(
            _ => new MySqlConduitConnectionFactory(connectionString));
        builder.Services.AddSingleton<ISqlDialect, MySqlDialect>();

        return builder;
    }
}
