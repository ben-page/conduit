using Conduit.Configuration;
using Microsoft.Extensions.Configuration;
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
    /// <param name="basePath">
    /// The base Conduit endpoint path. When not supplied, falls back to the endpoint's configured
    /// <c>BasePath</c> (<c>Conduit:Endpoints:{endpointName}:BasePath</c>), then to "/api/conduit".
    /// </param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static ConduitEndpointBuilder UseHttp(
        this ConduitEndpointBuilder builder,
        HttpClient httpClient,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(httpClient);

        // The explicit argument wins, then the configured BasePath, then the default —
        // an HttpClient supplied by the caller doesn't opt an endpoint out of configuration.
        var resolvedBasePath = basePath ?? builder.Configuration?["BasePath"] ?? "/api/conduit";

        builder.Services.AddKeyedSingleton(
            builder.EndpointName, new HttpTransport(httpClient, resolvedBasePath));

        return builder.UseTransport<HttpTransport>();
    }

    /// <summary>
    /// Configures this endpoint to dispatch over HTTP, taking the base address and path from the
    /// endpoint's configuration section. Registers a named <see cref="HttpClient"/> for the endpoint.
    /// </summary>
    /// <param name="builder">The endpoint builder.</param>
    /// <param name="configure">Optional callback to adjust the options after binding.</param>
    /// <returns>The endpoint builder for chaining.</returns>
    public static ConduitEndpointBuilder UseHttp(
        this ConduitEndpointBuilder builder,
        Action<HttpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new HttpOptions();

        // Configuration first, then the callback — code always wins.
        builder.Configuration?.Bind(options);
        configure?.Invoke(options);

        if (options.BaseAddress is null)
        {
            throw new InvalidOperationException(
                $"HTTP transport for endpoint '{builder.EndpointName}' has no base address. " +
                $"Set 'Conduit:Endpoints:{builder.EndpointName}:BaseAddress' in configuration " +
                $"or pass an HttpClient to UseHttp().");
        }

        var endpointName = builder.EndpointName;
        var baseAddress = options.BaseAddress;
        var basePath = options.BasePath;

        // Keyed by endpoint, matching the broker transports' pattern, so the bound options are
        // inspectable instead of being bound and dropped.
        builder.Services.AddKeyedSingleton(endpointName, options);

        builder.Services.AddHttpClient(endpointName, httpClient => httpClient.BaseAddress = baseAddress);

        builder.Services.AddKeyedSingleton(
            endpointName,
            (serviceProvider, _) => new HttpTransport(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(endpointName),
                basePath));

        return builder.UseTransport<HttpTransport>();
    }
}
