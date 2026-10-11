namespace Conduit.Transport.Http;

/// <summary>Endpoint metadata that <see cref="DispatchContextEndpointConventionBuilderExtensions.WithConduitDispatchContext{TBuilder}"/>
/// adds to an endpoint whose request delegate the method wrapped, so a host can assert -- e.g. in a test over its
/// <c>EndpointDataSource</c> -- that a route opted into the inbound dispatch context. The wrapped delegate is
/// invisible in endpoint metadata; this tag says the wrap happened.</summary>
public sealed class ConduitDispatchContextMetadata
{
    /// <summary>The single instance every opted-in endpoint carries.</summary>
    public static ConduitDispatchContextMetadata Instance { get; } = new();

    private ConduitDispatchContextMetadata() { }
}
