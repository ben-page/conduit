namespace Conduit.Runtime;

/// <summary>
/// Base type for all observation records emitted by the Conduit dispatch pipeline.
/// </summary>
/// <param name="Timestamp">When the observation occurred.</param>
public abstract record MessageObservation(DateTimeOffset Timestamp);

/// <summary>
/// Emitted when a dispatch operation is invoked.
/// </summary>
/// <param name="Timestamp">When the dispatch was invoked.</param>
public sealed record DispatchInvoked(DateTimeOffset Timestamp)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a dispatch operation completes successfully.
/// </summary>
/// <param name="Timestamp">When the dispatch completed.</param>
/// <param name="DurationMs">Total dispatch duration in milliseconds.</param>
public sealed record DispatchCompleted(DateTimeOffset Timestamp, double DurationMs)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a dispatch operation fails with an exception.
/// </summary>
/// <param name="Timestamp">When the failure occurred.</param>
/// <param name="DurationMs">Duration before failure in milliseconds.</param>
/// <param name="ErrorMessage">The exception message.</param>
/// <param name="ExceptionType">The full name of the exception type.</param>
public sealed record DispatchFailed(DateTimeOffset Timestamp, double DurationMs,
    string ErrorMessage, string ExceptionType)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when handler execution begins.
/// </summary>
/// <param name="Timestamp">When the handler started.</param>
/// <param name="HandlerType">The full name of the handler type.</param>
public sealed record HandlerStarted(DateTimeOffset Timestamp, string HandlerType)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when handler execution completes successfully.
/// </summary>
/// <param name="Timestamp">When the handler completed.</param>
/// <param name="DurationMs">Handler execution duration in milliseconds.</param>
public sealed record HandlerCompleted(DateTimeOffset Timestamp, double DurationMs)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when handler execution fails with an exception.
/// </summary>
/// <param name="Timestamp">When the failure occurred.</param>
/// <param name="ErrorMessage">The exception message.</param>
/// <param name="ExceptionType">The full name of the exception type.</param>
public sealed record HandlerFailed(DateTimeOffset Timestamp,
    string ErrorMessage, string ExceptionType)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a retry attempt is made by a resilience pipeline.
/// </summary>
/// <param name="Timestamp">When the retry was attempted.</param>
/// <param name="Attempt">The retry attempt number (1-based).</param>
/// <param name="ErrorMessage">The exception message from the failed attempt.</param>
/// <param name="ExceptionType">The full name of the exception type from the failed attempt.</param>
/// <param name="DelayMs">Delay before the retry in milliseconds, if any.</param>
public sealed record RetryAttempted(DateTimeOffset Timestamp, int Attempt,
    string ErrorMessage, string ExceptionType, double? DelayMs)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a transport send operation begins.
/// </summary>
/// <param name="Timestamp">When the send started.</param>
/// <param name="Transport">The transport type name.</param>
/// <param name="Endpoint">The target endpoint name.</param>
public sealed record TransportSendStarted(DateTimeOffset Timestamp,
    string Transport, string Endpoint)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a transport send operation completes successfully.
/// </summary>
/// <param name="Timestamp">When the send completed.</param>
/// <param name="DurationMs">Send duration in milliseconds.</param>
public sealed record TransportSendCompleted(DateTimeOffset Timestamp, double DurationMs)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a transport send operation fails.
/// </summary>
/// <param name="Timestamp">When the failure occurred.</param>
/// <param name="ErrorMessage">The exception message.</param>
/// <param name="ExceptionType">The full name of the exception type.</param>
public sealed record TransportSendFailed(DateTimeOffset Timestamp,
    string ErrorMessage, string ExceptionType)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when messages are written to the transactional outbox.
/// </summary>
/// <param name="Timestamp">When the outbox write occurred.</param>
/// <param name="EntryCount">The number of entries written.</param>
public sealed record OutboxWritten(DateTimeOffset Timestamp, int EntryCount)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when an outbox entry is successfully sent to the transport.
/// </summary>
/// <param name="Timestamp">When the entry was sent.</param>
/// <param name="EntryId">The outbox entry identifier.</param>
public sealed record OutboxSent(DateTimeOffset Timestamp, long EntryId)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when an outbox entry exhausts its retry attempts.
/// </summary>
/// <param name="Timestamp">When the retry was exhausted.</param>
/// <param name="EntryId">The outbox entry identifier.</param>
public sealed record OutboxRetryExhausted(DateTimeOffset Timestamp, long EntryId)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a duplicate message is detected by the inbox.
/// </summary>
/// <param name="Timestamp">When the duplicate was detected.</param>
public sealed record InboxDuplicate(DateTimeOffset Timestamp)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted when a message is moved to the dead letter queue.
/// </summary>
/// <param name="Timestamp">When the dead lettering occurred.</param>
/// <param name="Reason">The reason for dead lettering.</param>
/// <param name="SourceEndpoint">The endpoint that produced the dead letter.</param>
public sealed record DeadLettered(DateTimeOffset Timestamp,
    string Reason, string SourceEndpoint)
    : MessageObservation(Timestamp);

/// <summary>
/// Emitted for user-defined observation points with arbitrary data.
/// </summary>
/// <param name="Timestamp">When the observation occurred.</param>
/// <param name="Name">A dotted name identifying the observation point.</param>
/// <param name="Data">Arbitrary data associated with the observation.</param>
public sealed record CustomObservation(DateTimeOffset Timestamp,
    string Name, object? Data)
    : MessageObservation(Timestamp);
