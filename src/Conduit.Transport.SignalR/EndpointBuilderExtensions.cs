using Conduit.Configuration;

namespace Conduit.Transport.SignalR;

/// <summary>
/// Extension methods for configuring SignalR transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use SignalR for bidirectional messaging.
    /// When no URL is provided, this app hosts the hub and clients connect inbound.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseSignalR(this EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder;
    }
}
