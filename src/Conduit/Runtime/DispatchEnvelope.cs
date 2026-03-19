using System.Text.Json;

namespace Conduit.Runtime;

/// <summary>
/// A wire-format envelope containing a type discriminator and JSON payload,
/// used by HTTP and SignalR endpoints to route incoming requests.
/// </summary>
public sealed record DispatchEnvelope
{
    /// <summary>
    /// The fully-qualified or short type name of the request.
    /// </summary>
    public string Type { get; init; }

    /// <summary>
    /// The JSON payload to deserialize into the request type.
    /// </summary>
    public JsonElement Payload { get; init; }

    public DispatchEnvelope(string type, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        this.Type = type;
        this.Payload = payload;
    }
}
