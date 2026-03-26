using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http;

/// <summary>
/// Extension methods for configuring HTTP transport on endpoints.
/// </summary>
public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Configures this endpoint to use HTTP POST to a remote Conduit dispatch endpoint.
    /// The <paramref name="httpClient"/> is captured at configuration time and registered
    /// as a keyed singleton — its lifetime is tied to the DI container.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="httpClient">An HttpClient with BaseAddress set to the remote service.</param>
    /// <param name="basePath">The base Conduit endpoint path. Defaults to "/api/conduit".</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static EndpointBuilder UseHttp(
        this EndpointBuilder builder,
        HttpClient httpClient,
        string basePath = "/api/conduit")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(httpClient);

        builder.Services.AddKeyedSingleton(
            builder.EndpointName, new HttpTransport(httpClient, basePath));

        return builder.UseTransport<HttpTransport>();
    }
}
