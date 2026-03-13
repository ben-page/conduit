using Conduit.Configuration;

namespace Conduit.Routing;

/// <summary>
/// Resolves a message type to a remote endpoint, or returns null for local dispatch.
/// </summary>
public interface IRouteResolver
{
    /// <summary>
    /// Returns the endpoint that should handle this message type,
    /// or <see langword="null"/> if the message should be dispatched locally.
    /// </summary>
    /// <param name="messageFullTypeName">The full type name of the message (e.g. "Orders.Commands.CreateOrder").</param>
    /// <returns>The target endpoint, or null for local dispatch.</returns>
    public ConduitEndpoint? Resolve(string messageFullTypeName);
}
