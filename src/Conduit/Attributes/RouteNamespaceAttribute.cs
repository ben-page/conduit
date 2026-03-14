namespace Conduit.Attributes;

/// <summary>
/// Routes all message types in the specified namespace (and children)
/// to the named endpoint for remote dispatch.
/// The endpoint must be configured via <c>AddEndpoint</c> at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RouteNamespaceAttribute(string @namespace, string endpointName) : Attribute
{
    /// <summary>
    /// The namespace prefix to match against message type namespaces.
    /// </summary>
    public string Namespace => @namespace;

    /// <summary>
    /// The name of the endpoint to route matching types to.
    /// </summary>
    public string EndpointName => endpointName;
}
