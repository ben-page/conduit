namespace Conduit.Exceptions;

/// <summary>
/// Thrown when one or more event listeners fail: by a local <c>PublishAsync</c> (to the publisher) and
/// by wire event dispatch. A <c>[FireAndForget]</c> event's background listeners are the exception: their
/// failure is logged, as nothing awaits them.
/// Not a <see cref="ConduitException"/> — this is infrastructure, not a domain error.
/// Transports catch this to decide whether to complete or abandon the broker message.
/// <see cref="Exception.InnerException"/> is the first listener failure in run order, so
/// <see cref="Exception.GetBaseException"/> reaches its root cause.
/// </summary>
public sealed class EventListenerException(
    string message,
    IReadOnlyList<Exception> listenerExceptions) : Exception(message, FirstOf(listenerExceptions))
{
    /// <summary>
    /// The exceptions thrown by individual listeners.
    /// </summary>
    public IReadOnlyList<Exception> ListenerExceptions => listenerExceptions;

    // Runs in the base constructor call, before any other use of the list: the guard must be here, or a null list is
    // a NullReferenceException from the constructor
    private static Exception? FirstOf(IReadOnlyList<Exception> listenerExceptions)
    {
        ArgumentNullException.ThrowIfNull(listenerExceptions);
        return listenerExceptions.Count > 0 ? listenerExceptions[0] : null;
    }
}
