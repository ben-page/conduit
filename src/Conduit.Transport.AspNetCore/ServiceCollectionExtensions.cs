using Conduit.Configuration;
using Conduit.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AspNetCore;

/// <summary>
/// Extension methods for registering Conduit services with ASP.NET Core dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Conduit mediator infrastructure with endpoint routing.
    /// Call <c>AddConduitSignalR()</c> from <c>Conduit.Transport.SignalR</c> to add real-time transport.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional builder callback for endpoint and route configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduit(
        this IServiceCollection services,
        Action<ConduitBuilder>? configure = null)
    {
        var builder = new ConduitBuilder(services);
        configure?.Invoke(builder);

        // Register routing infrastructure
        services.AddSingleton(builder.Options);
        services.AddSingleton<IRouteResolver, DefaultRouteResolver>();

        return services;
    }
}
