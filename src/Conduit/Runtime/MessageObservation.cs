namespace Conduit.Runtime;

/// <summary>
/// Base type for all observation records emitted by the Conduit dispatch pipeline.
/// Auto-captures <see cref="Timestamp"/> as <see cref="DateTime.UtcNow"/> on creation.
/// </summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DispatchInvoked), "DispatchInvoked")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DispatchCompleted), "DispatchCompleted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DispatchFailed), "DispatchFailed")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(HandlerStarted), "HandlerStarted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(HandlerCompleted), "HandlerCompleted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(HandlerFailed), "HandlerFailed")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RetryAttempted), "RetryAttempted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(TransportSendStarted), "TransportSendStarted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(TransportSendCompleted), "TransportSendCompleted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(TransportSendFailed), "TransportSendFailed")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(OutboxWritten), "OutboxWritten")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(OutboxSent), "OutboxSent")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(OutboxRetryExhausted), "OutboxRetryExhausted")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(InboxDuplicate), "InboxDuplicate")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SelfPublishedEventSkipped), "SelfPublishedEventSkipped")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DeadLettered), "DeadLettered")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CustomObservation), "CustomObservation")]
public abstract record MessageObservation
{
    /// <summary>When the observation occurred (UTC).</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Emitted when a dispatch operation is invoked.
/// </summary>
/// <param name="MessageType">The full type name of the dispatched message.</param>
/// <param name="MessageKind">The kind of message: Command, Query, or Event.</param>
public sealed record DispatchInvoked(string MessageType, string MessageKind)
    : MessageObservation;

/// <summary>
/// Emitted when a dispatch operation completes successfully.
/// </summary>
/// <param name="DurationMs">Total dispatch duration in milliseconds.</param>
public sealed record DispatchCompleted(double DurationMs)
    : MessageObservation;

/// <summary>
/// Emitted when a dispatch operation fails with an exception.
/// </summary>
/// <param name="DurationMs">Duration before failure in milliseconds.</param>
/// <param name="ErrorMessage">The exception message.</param>
/// <param name="ExceptionType">The full name of the exception type.</param>
public sealed record DispatchFailed(double DurationMs,
    string ErrorMessage, string ExceptionType)
    : MessageObservation;

/// <summary>
/// Emitted when handler execution begins.
/// </summary>
/// <param name="HandlerType">The full name of the handler type.</param>
public sealed record HandlerStarted(string HandlerType)
    : MessageObservation;

/// <summary>
/// Emitted when handler execution completes successfully.
/// </summary>
/// <param name="DurationMs">Handler execution duration in milliseconds.</param>
public sealed record HandlerCompleted(double DurationMs)
    : MessageObservation;

/// <summary>
/// Emitted when handler execution fails with an exception.
/// </summary>
/// <param name="ErrorMessage">The exception message.</param>
/// <param name="ExceptionType">The full name of the exception type.</param>
public sealed record HandlerFailed(string ErrorMessage, string ExceptionType)
    : MessageObservation;

/// <summary>
/// Emitted when a retry attempt is made by a resilience pipeline.
/// </summary>
/// <param name="Attempt">The retry attempt number (1-based).</param>
/// <param name="ErrorMessage">The exception message from the failed attempt.</param>
/// <param name="ExceptionType">The full name of the exception type from the failed attempt.</param>
/// <param name="DelayMs">Delay before the retry in milliseconds, if any.</param>
public sealed record RetryAttempted(int Attempt,
    string ErrorMessage, string ExceptionType, double? DelayMs)
    : MessageObservation;

/// <summary>
/// Emitted when a transport send operation begins.
/// </summary>
/// <param name="Transport">The transport type name.</param>
/// <param name="Endpoint">The target endpoint name.</param>
public sealed record TransportSendStarted(string Transport, string Endpoint)
    : MessageObservation;

/// <summary>
/// Emitted when a transport send operation completes successfully.
/// </summary>
/// <param name="DurationMs">Send duration in milliseconds.</param>
public sealed record TransportSendCompleted(double DurationMs)
    : MessageObservation;

/// <summary>
/// Emitted when a transport send operation fails.
/// </summary>
/// <param name="ErrorMessage">The exception message.</param>
/// <param name="ExceptionType">The full name of the exception type.</param>
public sealed record TransportSendFailed(string ErrorMessage, string ExceptionType)
    : MessageObservation;

/// <summary>
/// Emitted when messages are written to the transactional outbox.
/// </summary>
/// <param name="EntryCount">The number of entries written.</param>
public sealed record OutboxWritten(int EntryCount)
    : MessageObservation;

/// <summary>
/// Emitted when an outbox entry is successfully sent to the transport.
/// </summary>
/// <param name="EntryId">The outbox entry identifier.</param>
public sealed record OutboxSent(long EntryId)
    : MessageObservation;

/// <summary>
/// Emitted when an outbox entry exhausts its retry attempts.
/// </summary>
/// <param name="EntryId">The outbox entry identifier.</param>
public sealed record OutboxRetryExhausted(long EntryId)
    : MessageObservation;

/// <summary>
/// Emitted when a duplicate message is detected by the inbox.
/// </summary>
public sealed record InboxDuplicate()
    : MessageObservation;

/// <summary>
/// Emitted when an inbound event is dropped because this application published it — its local
/// listeners already ran inline at publish time.
/// </summary>
/// <param name="MessageType">The full type name of the skipped event.</param>
public sealed record SelfPublishedEventSkipped(string MessageType)
    : MessageObservation;

/// <summary>
/// Emitted when a message is moved to the dead letter queue.
/// </summary>
/// <param name="Reason">The reason for dead lettering.</param>
/// <param name="SourceEndpoint">The endpoint that produced the dead letter.</param>
public sealed record DeadLettered(string Reason, string SourceEndpoint)
    : MessageObservation;

/// <summary>
/// Emitted for user-defined observation points with arbitrary data.
/// </summary>
/// <param name="Name">A dotted name identifying the observation point.</param>
/// <param name="Data">Arbitrary data associated with the observation.</param>
public sealed record CustomObservation(string Name, object? Data)
    : MessageObservation;
