namespace Conduit.Configuration;

/// <summary>
/// Fluent builder for routing messages from a namespace to an endpoint.
/// </summary>
public sealed class RouteBuilder
{
    private readonly ConduitOptions options;
    private readonly string pattern;

    internal RouteBuilder(ConduitOptions options, string pattern)
    {
        this.options = options;
        this.pattern = pattern;
    }

    /// <summary>
    /// Routes messages matching this pattern to the named endpoint.
    /// </summary>
    /// <param name="endpointName">The endpoint name to route to.</param>
    public void To(string endpointName)
    {
        this.options.Routes.Add(new ConduitRoute
        {
            Pattern = this.pattern,
            EndpointName = endpointName
        });
    }
}
