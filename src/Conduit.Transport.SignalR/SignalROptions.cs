namespace Conduit.Transport.SignalR;

/// <summary>
/// Configuration for the SignalR client transport. Bound from the endpoint's configuration
/// section and/or supplied to <c>UseSignalR()</c>.
/// </summary>
public sealed class SignalROptions
{
    /// <summary>
    /// The URI of the remote SignalR hub, e.g. <c>https://inventory.example/conduit</c>.
    /// </summary>
    public Uri? HubUri { get; set; }
}
