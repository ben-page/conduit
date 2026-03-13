using Microsoft.Extensions.DependencyInjection;

namespace Conduit.AspNetCore;

/// <summary>
/// Extension methods for registering Conduit services with ASP.NET Core dependency injection.
/// </summary>
public static class ConduitAspNetCoreExtensions
{
    /// <summary>
    /// Adds Conduit mediator infrastructure including SignalR event transport.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional builder callback for additional configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduit(
        this IServiceCollection services,
        Action<ConduitBuilder>? configure = null)
    {
        var builder = new ConduitBuilder(services);
        configure?.Invoke(builder);

        services.AddSignalR();
        services.AddSingleton<IEventTransport, SignalREventTransport>();

        return services;
    }
}
