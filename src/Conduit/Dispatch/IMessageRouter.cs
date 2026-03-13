using System.Text.Json;

namespace Conduit.Dispatch;

/// <summary>
/// Routes commands, queries, and events received over the wire (HTTP, SignalR, etc.)
/// by deserializing a JSON payload into the appropriate request type and dispatching
/// it through the mediator. Implemented by the source generator.
/// </summary>
public interface IDispatchRouter
{
    /// <summary>
    /// Deserializes <paramref name="payload"/> into the command or query type identified
    /// by <paramref name="type"/> and dispatches it through the mediator.
    /// </summary>
    /// <param name="type">The fully-qualified or short type name of the command or query.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handler result, or <see langword="null"/> for void commands.</returns>
    public Task<object?> DispatchRequestAsync(string type, JsonElement payload, CancellationToken cancellationToken);

    /// <summary>
    /// Deserializes <paramref name="payload"/> into the event type identified
    /// by <paramref name="type"/> and publishes it to all registered listeners.
    /// </summary>
    /// <param name="type">The short type name of the event.</param>
    /// <param name="payload">The JSON payload to deserialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task DispatchEventAsync(string type, JsonElement payload, CancellationToken cancellationToken);
}
