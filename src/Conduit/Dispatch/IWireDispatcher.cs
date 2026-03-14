using System.Text.Json;

namespace Conduit.Dispatch;

/// <summary>
/// Deserializes wire payloads and dispatches them to local handlers.
/// Takes a type name string + JSON payload, deserializes into the strongly-typed
/// message, and calls the appropriate handler. Implemented by the source-generated mediator.
/// </summary>
public interface IWireDispatcher
{
    /// <summary>
    /// Deserializes <paramref name="payload"/> into the command or query type identified
    /// by <paramref name="type"/> and dispatches it to its handler.
    /// </summary>
    /// <param name="type">The fully-qualified type name of the command or query.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handler result, or <see langword="null"/> for void commands.</returns>
    public Task<object?> DispatchRequestAsync(string type, JsonElement payload, CancellationToken cancellationToken);

    /// <summary>
    /// Deserializes <paramref name="payload"/> into the event type identified
    /// by <paramref name="type"/> and publishes it to all registered listeners.
    /// </summary>
    /// <param name="type">The fully-qualified type name of the event.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task DispatchEventAsync(string type, JsonElement payload, CancellationToken cancellationToken);
}
