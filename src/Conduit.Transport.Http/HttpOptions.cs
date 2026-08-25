namespace Conduit.Transport.Http;

/// <summary>
/// Configuration for the HTTP client transport. Bound from the endpoint's configuration
/// section and/or supplied to <c>UseHttp()</c>.
/// </summary>
public sealed class HttpOptions
{
    /// <summary>
    /// Base address of the remote Conduit host, e.g. <c>https://billing.example</c>.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Path prefix the remote host maps its dispatch endpoint on. Default <c>/api/conduit</c>.
    /// </summary>
    public string BasePath { get; set; } = "/api/conduit";
}
