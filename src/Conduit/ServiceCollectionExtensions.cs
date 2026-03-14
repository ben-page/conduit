using Conduit.Configuration;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit;

/// <summary>
/// Extension methods for registering Conduit core services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a type filter interceptor that restricts which message types
    /// external clients are permitted to dispatch.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure allowed types.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduitTypeFilter(
        this IServiceCollection services,
        Action<TypeFilterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new TypeFilterBuilder();
        configure(builder);

        services.Configure<TypeFilterOptions>(options =>
        {
            foreach (var type in builder.Options.AllowedTypes)
            {
                options.AllowedTypes.Add(type);
            }
        });

        services.AddSingleton<IWireInterceptor, TypeFilterInterceptor>();

        return services;
    }
}
