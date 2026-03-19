using Conduit.Runtime;
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
    /// Configures SignalR JSON serialization to match <see cref="ConduitRuntime.JsonOptions"/>
    /// (camelCase, string enums, case-insensitive).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConduitSignalR(this IServiceCollection services)
    {
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
        services.AddSingleton<SignalRTransport>();
        services.AddSingleton<IConduitTransport>(sp => sp.GetRequiredService<SignalRTransport>());
        return services;
    }
}
