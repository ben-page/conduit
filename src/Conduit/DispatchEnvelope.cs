using System.Text.Json;

namespace Conduit;

public sealed record DispatchEnvelope(string Type, JsonElement Payload);
