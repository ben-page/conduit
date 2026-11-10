namespace Conduit.Runtime;

/// <summary>
/// A wire event's dispatch failed outside its listeners: a pipeline behavior threw, its listeners could not be resolved,
/// a gate (<see cref="Middleware.IInboundInterceptor"/>) threw something other than a refusal, or reading or capturing
/// the event failed unexpectedly. Thrown by <see cref="IWireMediator.HandleWireEventAsync"/> after the runtime logged
/// <see cref="Exception.InnerException"/>, the original failure, at Error (EventId 2, <c>PublishError</c>) under
/// <see cref="Reference"/>.
/// </summary>
/// <remarks>
/// Not a <see cref="Exceptions.ConduitException"/>, which is a refusal, and not an
/// <see cref="Exceptions.EventListenerException"/>, which a listener's own failure is: the transport settles it as an
/// unexpected failure. A broker transport captures it, else abandons it, as a failed listener; HTTP <c>/publish</c>
/// and the SignalR hub's <c>Publish</c> answer <c>InternalError</c> with the fixed message, as <c>/dispatch</c> answers
/// an unexpected failure, never the exception.
/// </remarks>
public sealed class EventDispatchException : Exception
{
    /// <summary>Creates the exception for a failure the runtime logged under <paramref name="reference"/>.</summary>
    /// <param name="messageType">The event's wire type name.</param>
    /// <param name="reference">The support reference the failure was logged under.</param>
    /// <param name="innerException">The original failure.</param>
    public EventDispatchException(string messageType, string reference, Exception innerException)
        : base(
            $"Dispatching event '{messageType}' failed outside its listeners (reference {reference}).",
            innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        this.MessageType = messageType;
        this.Reference = reference;
    }

    /// <summary>The event's wire type name.</summary>
    public string MessageType { get; }

    /// <summary>The support reference the runtime logged the failure under: quotable by a publisher, greppable in the
    /// log line that carries the same value.</summary>
    public string Reference { get; }
}
