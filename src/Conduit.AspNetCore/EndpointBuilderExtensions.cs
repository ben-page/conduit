using Conduit.Routing;

namespace Conduit.AspNetCore;

/// <summary>
/// Extension methods for configuring ASP.NET Core transports on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use HTTP POST to a remote Conduit dispatch endpoint.
    /// The caller is responsible for managing the <paramref name="httpClient"/> lifetime
    /// (typically via <c>IHttpClientFactory</c>).
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="httpClient">An HttpClient with BaseAddress set to the remote service.</param>
    /// <param name="path">The dispatch endpoint path. Defaults to "/api/dispatch".</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseHttp(
        this EndpointBuilder builder,
        HttpClient httpClient,
        string path = "/api/dispatch")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(httpClient);
        var transport = new HttpTransport(httpClient, path);
        return builder.UseTransport(transport);
    }

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
