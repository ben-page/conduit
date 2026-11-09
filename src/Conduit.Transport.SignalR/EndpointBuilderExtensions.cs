using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Extension methods for configuring SignalR transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to connect to a remote SignalR hub, taking the hub URI from the
    /// endpoint's configuration section unless one is supplied here.
    /// Creates a <see cref="HubConnection"/> in client mode for outbound C#-to-C# communication.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="hubUri">The hub URI. Overrides the configured value when given.</param>
    /// <param name="configure">Optional callback to adjust the options after binding.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Transport lifetime is managed by DI via keyed singleton registration.")]
    public static ConduitEndpointBuilder UseSignalR(
        this ConduitEndpointBuilder builder,
        Uri? hubUri = null,
        Action<SignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SignalROptions();

        // Configuration first, then the explicit argument, then the callback — code always wins.
        builder.Configuration?.Bind(options);

        if (hubUri is not null)
        {
            options.HubUri = hubUri;
        }

        configure?.Invoke(options);

        if (options.HubUri is null)
        {
            throw new InvalidOperationException(
                $"SignalR transport for endpoint '{builder.EndpointName}' has no hub URI. " +
                $"Set 'Conduit:Endpoints:{builder.EndpointName}:HubUri' in configuration " +
                $"or pass it to UseSignalR().");
        }

        // Keyed by endpoint, matching the broker transports' pattern, so the bound options are
        // inspectable instead of being bound and dropped.
        builder.Services.AddKeyedSingleton(builder.EndpointName, options);

        builder.Services.AddKeyedSingleton(
            builder.EndpointName, new SignalRTransport(BuildHubConnection(options.HubUri)));

        return builder.UseTransport<SignalRTransport>();
    }

    private static HubConnection BuildHubConnection(Uri hubUri)
    {
        return new HubConnectionBuilder()
            .WithUrl(hubUri)
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
            })
            .WithAutomaticReconnect()
            .Build();
    }
}
