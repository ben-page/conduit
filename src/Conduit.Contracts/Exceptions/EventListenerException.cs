namespace Conduit.Exceptions;

/// <summary>
/// Thrown by wire event dispatch when one or more event listeners fail.
/// Not a <see cref="ConduitException"/> — this is infrastructure, not a domain error.
/// Transports catch this to decide whether to complete or abandon the broker message.
/// </summary>
public sealed class EventListenerException(
    string message,
    IReadOnlyList<Exception> listenerExceptions) : Exception(message)
{
    /// <summary>
    /// The exceptions thrown by individual listeners.
    /// </summary>
    public IReadOnlyList<Exception> ListenerExceptions => listenerExceptions;
}
