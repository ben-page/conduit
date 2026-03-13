using Conduit.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Extension methods for registering Conduit SignalR transport services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds SignalR transport for Conduit, enabling real-time event delivery to connected clients
    /// and client-to-server dispatch via the <see cref="ConduitHub"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduitSignalR(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<SignalRTransport>();
        services.AddSingleton<IConduitTransport>(sp => sp.GetRequiredService<SignalRTransport>());
        return services;
    }
}
