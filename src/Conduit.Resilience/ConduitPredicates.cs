using Conduit.Exceptions;
using Conduit.Runtime;
using Polly;

namespace Conduit.Resilience;

/// <summary>
/// Pre-built Polly v8 predicates that understand Conduit's exception hierarchy.
/// Each method returns a fresh <see cref="PredicateBuilder{T}"/> instance so callers
/// can safely chain additional <c>.Handle&lt;TException&gt;()</c> calls without mutating a shared instance.
/// </summary>
public static class ConduitPredicates
{
    /// <summary>
    /// Handles the failures a rerun can fix: a <see cref="TimeoutException"/> (a broker request with no reply within
    /// its <c>ResponseTimeout</c> throws one), and a <see cref="RemoteDispatchException"/> whose
    /// <see cref="RemoteDispatchException.Code"/> is <see cref="ErrorCode.InternalError"/> (the remote endpoint failed
    /// unexpectedly). Does not handle a remote refusal (a <see cref="RemoteDispatchException"/> with any other code:
    /// <c>NotFound</c>, <c>Conflict</c>, <c>Unauthorized</c>, <c>PreconditionFailed</c>, ...), a local
    /// <see cref="ConduitException"/> (<see cref="NotFoundException"/>, <see cref="UnauthorizedException"/>,
    /// <see cref="ConflictException"/>, <see cref="PreconditionFailedException"/>, ...) or any other
    /// <see cref="InvalidOperationException"/>: those are outcomes, and resending them gets the same answer.
    /// </summary>
    /// <typeparam name="T">
    /// The result type of the resilience pipeline. For exception-only predicates the
    /// result type does not affect which exceptions are handled — it only needs to match
    /// the <see cref="Polly.Retry.RetryStrategyOptions{TResult}"/> type parameter.
    /// </typeparam>
    public static PredicateBuilder<T> TransientErrors<T>() =>
        new PredicateBuilder<T>()
            .Handle<TimeoutException>()
            .Handle<RemoteDispatchException>(exception => exception.Code == ErrorCode.InternalError);

    /// <summary>
    /// Handles all exceptions except <see cref="OperationCanceledException"/>.
    /// Use when you want to retry on any failure that isn't an explicit cancellation.
    /// </summary>
    /// <typeparam name="T">
    /// The result type of the resilience pipeline. For exception-only predicates the
    /// result type does not affect which exceptions are handled.
    /// </typeparam>
    public static PredicateBuilder<T> AllErrors<T>() =>
        new PredicateBuilder<T>()
            .Handle<Exception>(ex => ex is not OperationCanceledException);
}
