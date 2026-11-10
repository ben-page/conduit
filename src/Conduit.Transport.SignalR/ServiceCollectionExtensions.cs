using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Extension methods for registering Conduit SignalR transport services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds SignalR transport for Conduit, enabling real-time event delivery to connected clients
    /// and client-to-server dispatch via the <see cref="ConduitHub"/>.
    /// Configures SignalR JSON serialization to match <see cref="ConduitRuntime.JsonOptions"/>
    /// (camelCase, string enums, case-insensitive).
    /// A published event is pushed to every connection when it is <c>[ClientVisible]</c> and to none otherwise, unless
    /// an <see cref="IEventRecipientResolver"/> is registered, which then decides for every event.
    /// </summary>
    /// <remarks>
    /// The transport is added once, however often this is called: a second call (another module of the same host, say)
    /// registers nothing twice, so an event is still pushed once, and its <paramref name="configure"/> callback applies
    /// after the first call's.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures how the hub treats its callers (<see cref="ConduitSignalROptions"/>); by
    /// default it mints every dispatch's correlation ids and ignores the caller's. Callbacks from several calls all
    /// apply, in order.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduitSignalR(
        this IServiceCollection services, Action<ConduitSignalROptions>? configure = null)
    {
        services.AddOptions<ConduitSignalROptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // Added once: a second non-keyed forwarder would have the generated publish broadcast every event to the hub
        // twice. (Client-mode SignalRTransports are keyed per endpoint, so they never match.)
        if (services.Any(descriptor => descriptor.ServiceType == typeof(SignalRTransport) && !descriptor.IsKeyedService))
        {
            return services;
        }

        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy =
                    ConduitRuntime.JsonOptions.PropertyNamingPolicy;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive =
                    ConduitRuntime.JsonOptions.PropertyNameCaseInsensitive;
                foreach (var converter in ConduitRuntime.JsonOptions.Converters)
                {
                    options.PayloadSerializerOptions.Converters.Add(converter);
                }
            });

        // A factory, not the constructor DI would pick: the resolver is optional and resolved here, at the one place that
        // builds the server-mode transport. The registry is AddConduit's.
        services.AddSingleton(serviceProvider => new SignalRTransport(
            serviceProvider.GetRequiredService<IHubContext<ConduitHub>>(),
            serviceProvider.GetRequiredService<ConduitTypeRegistry>(),
            serviceProvider.GetRequiredService<ILogger<SignalRTransport>>(),
            serviceProvider.GetService<IEventRecipientResolver>()));
        services.AddSingleton<IConduitTransport>(serviceProvider => serviceProvider.GetRequiredService<SignalRTransport>());
        return services;
    }

    /// <summary>
    /// Adds SignalR transport for Conduit, and has <see cref="ConduitHub"/> send <paramref name="serverInfo"/> to every
    /// client as it connects and reconnects. Conduit carries the value and never derives it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serverInfo">What the hub tells each client about this server.</param>
    /// <param name="configure">Configures how the hub treats its callers, as
    /// <see cref="AddConduitSignalR(IServiceCollection, Action{ConduitSignalROptions}?)"/> does.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduitSignalR(
        this IServiceCollection services, ConduitServerInfo serverInfo, Action<ConduitSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(serverInfo);
        services.AddConduitSignalR(configure);
        services.AddSingleton(serverInfo);
        return services;
    }
}
