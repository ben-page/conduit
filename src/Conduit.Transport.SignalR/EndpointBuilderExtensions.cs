using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Extension methods for configuring SignalR transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to connect to a remote SignalR hub at the given URI.
    /// Creates a <see cref="HubConnection"/> in client mode for outbound C#-to-C# communication.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="hubUri">The URI of the remote SignalR hub.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Transport lifetime is managed by DI via keyed singleton registration.")]
    public static EndpointBuilder UseSignalR(this EndpointBuilder builder, Uri hubUri)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hubUri);

        var hubConnection = new HubConnectionBuilder()
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

        builder.Services.AddKeyedSingleton(
            builder.EndpointName, new SignalRTransport(hubConnection));

        return builder.UseTransport<SignalRTransport>();
    }
}
