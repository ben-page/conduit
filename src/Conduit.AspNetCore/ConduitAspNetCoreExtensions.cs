using Microsoft.Extensions.DependencyInjection;

namespace Conduit.AspNetCore;

public static class ConduitAspNetCoreExtensions
{
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
