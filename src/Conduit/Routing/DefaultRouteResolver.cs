using Conduit.Configuration;

namespace Conduit.Routing;

/// <summary>
/// Default route resolver that matches message type names against configured route patterns.
/// Routes are evaluated in order; first matching pattern wins.
/// A pattern matches if the message full type name starts with the pattern string.
/// </summary>
public sealed class DefaultRouteResolver(ConduitOptions options) : IRouteResolver
{
    /// <inheritdoc />
    public ConduitEndpoint? Resolve(string messageFullTypeName)
    {
        ArgumentNullException.ThrowIfNull(messageFullTypeName);

        foreach (var route in options.Routes)
        {
            if (messageFullTypeName.StartsWith(route.Pattern, StringComparison.Ordinal))
            {
                if (!options.Endpoints.TryGetValue(route.EndpointName, out var endpoint))
                {
                    throw new InvalidOperationException(
                        $"Route pattern '{route.Pattern}' matched message '{messageFullTypeName}' " +
                        $"but endpoint '{route.EndpointName}' is not registered. " +
                        $"Ensure AddEndpoint(\"{route.EndpointName}\", ...) is called in your Conduit configuration.");
                }

                if (endpoint.Transport is null)
                {
                    throw new InvalidOperationException(
                        $"Endpoint '{route.EndpointName}' has no transport configured. " +
                        $"Call UseHttp() or UseSignalR(uri) in the endpoint configuration.");
                }

                return endpoint;
            }
        }

        return null;
    }
}
