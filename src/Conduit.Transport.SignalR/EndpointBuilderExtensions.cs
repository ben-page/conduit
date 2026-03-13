using Conduit.Configuration;
using Microsoft.AspNetCore.SignalR.Client;

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
        Justification = "Transport ownership is transferred to the endpoint via UseTransport.")]
    public static EndpointBuilder UseSignalR(this EndpointBuilder builder, Uri hubUri)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hubUri);

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri)
            .WithAutomaticReconnect()
            .Build();

        var transport = new SignalRTransport(hubConnection);
        return builder.UseTransport(transport);
    }
}
