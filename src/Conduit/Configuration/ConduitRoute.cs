namespace Conduit.Configuration;

/// <summary>
/// Maps a message type, or a namespace of message types, to a named endpoint.
/// Messages matching the route are sent to the endpoint's transport instead of being
/// dispatched locally. Created by <see cref="EndpointBuilder.RouteType{T}"/> (exact) and
/// <see cref="EndpointBuilder.RouteNamespace{T}"/> (namespace).
/// </summary>
/// <param name="Pattern">
/// The full type name (exact) or namespace (not exact) to match against a message's full type name.
/// </param>
/// <param name="EndpointName">
/// The name of the endpoint to route matching messages to.
/// Must correspond to an endpoint registered via <see cref="ConduitBuilder.AddEndpoint"/>.
/// </param>
/// <param name="Exact">
/// True to match only the type whose full name equals <paramref name="Pattern"/>. False to match
/// every type in the <paramref name="Pattern"/> namespace and the namespaces beneath it, on whole
/// segments: <c>"Orders.Commands"</c> matches <c>Orders.Commands.CreateOrder</c> and
/// <c>Orders.Commands.Lines.AddLine</c>, never <c>Orders.CommandsArchive.ArchiveOrder</c>.
/// </param>
public sealed record ConduitRoute(string Pattern, string EndpointName, bool Exact)
{
    /// <summary>Whether this route matches the message whose full type name is <paramref name="messageFullTypeName"/>.</summary>
    internal bool Matches(string messageFullTypeName)
        => this.Exact
            ? string.Equals(messageFullTypeName, this.Pattern, StringComparison.Ordinal)
            : string.Equals(messageFullTypeName, this.Pattern, StringComparison.Ordinal)
                || (messageFullTypeName.Length > this.Pattern.Length
                    && messageFullTypeName[this.Pattern.Length] == '.'
                    && messageFullTypeName.StartsWith(this.Pattern, StringComparison.Ordinal));
}
