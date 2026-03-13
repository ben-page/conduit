using Conduit.Dispatch;

namespace Conduit.Routing;

/// <summary>
/// Bidirectional transport for delivering messages between Conduit nodes.
/// Implementations handle specific protocols (SignalR, HTTP, Rebus, etc.).
/// </summary>
public interface IConduitTransport
{
    /// <summary>
    /// Sends a command or query to a remote endpoint and returns the result.
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing the type and payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handler result, or <see langword="null"/> for void commands.</returns>
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Delivers a message (typically an event) to external consumers (fire-and-forget).
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing the type and payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SendMessageAsync(DispatchEnvelope envelope, CancellationToken cancellationToken);
}
