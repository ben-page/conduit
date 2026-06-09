namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// Singleton sink that captures the dispatch <c>MessageContext.Properties</c> observed
/// by <see cref="CapturePropertiesHandler"/> for a single inbound dispatch.
/// </summary>
public sealed class CapturedProperties
{
    public IReadOnlyDictionary<string, object?>? Properties { get; set; }
}
