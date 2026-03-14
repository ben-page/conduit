namespace Conduit.Attributes;

/// <summary>
/// Routes this message type to the named endpoint for remote dispatch.
/// The endpoint must be configured via <c>AddEndpoint</c> at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteAttribute(string endpointName) : Attribute
{
    /// <summary>
    /// The name of the endpoint to route this type to.
    /// </summary>
    public string EndpointName => endpointName;
}
