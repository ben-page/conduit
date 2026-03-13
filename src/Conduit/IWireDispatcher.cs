using System.Text.Json;

namespace Conduit;

/// <summary>
/// Dispatches commands and queries received over the wire (HTTP, SignalR, etc.)
/// by deserializing a JSON payload into the appropriate request type and routing
/// it through the mediator. Implemented by the source generator.
/// </summary>
public interface IWireDispatcher
{
    /// <summary>
    /// Deserializes <paramref name="payload"/> into the request type identified
    /// by <paramref name="type"/> and dispatches it through the mediator.
    /// </summary>
    /// <param name="type">The fully-qualified or short type name of the request.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handler result, or <see langword="null"/> for void commands.</returns>
    public Task<object?> DispatchAsync(string type, JsonElement payload, CancellationToken cancellationToken);
}
