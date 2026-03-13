using Conduit.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Bridge.SignalR;

/// <summary>
/// Extension methods for registering bridge interceptors.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds bridge interceptors that filter dispatch requests from browser clients.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure allowed types and bridge behavior.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduitBridge(
        this IServiceCollection services,
        Action<BridgeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new BridgeBuilder();
        configure(builder);

        services.Configure<BridgeOptions>(options =>
        {
            foreach (var type in builder.Options.AllowedTypes)
            {
                options.AllowedTypes.Add(type);
            }
        });

        services.AddSingleton<IDispatchInterceptor, BridgeTypeFilterInterceptor>();

        return services;
    }
}
