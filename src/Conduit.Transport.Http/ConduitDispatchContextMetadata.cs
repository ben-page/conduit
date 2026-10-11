namespace Conduit.Transport.Http;

/// <summary>Endpoint metadata that <see cref="DispatchContextEndpointConventionBuilderExtensions.WithConduitDispatchContext{TBuilder}"/>
/// adds beside its filter, so a host can assert -- e.g. in a test over its <c>EndpointDataSource</c> -- that a route
/// opted into the inbound dispatch context. The filter itself runs only on route-handler endpoints (MapGet/MapPost
/// delegates); the metadata says the route was opted in.</summary>
public sealed class ConduitDispatchContextMetadata
{
    /// <summary>The single instance every opted-in endpoint carries.</summary>
    public static ConduitDispatchContextMetadata Instance { get; } = new();

    private ConduitDispatchContextMetadata() { }
}
