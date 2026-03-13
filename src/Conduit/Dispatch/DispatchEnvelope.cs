using System.Text.Json;

namespace Conduit.Dispatch;

/// <summary>
/// A wire-format envelope containing a type discriminator and JSON payload,
/// used by HTTP and SignalR endpoints to route incoming requests.
/// </summary>
/// <param name="Type">The fully-qualified or short type name of the request.</param>
/// <param name="Payload">The JSON payload to deserialize into the request type.</param>
public sealed record DispatchEnvelope(string Type, JsonElement Payload);
