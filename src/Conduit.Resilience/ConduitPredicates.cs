using Conduit.Exceptions;
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
    /// Handles transient infrastructure failures (<see cref="TimeoutException"/> and
    /// <see cref="InvalidOperationException"/>) that are safe to retry.
    /// Does not handle <see cref="ConduitException"/> subtypes — those are business
    /// outcomes (<see cref="NotFoundException"/>, <see cref="UnauthorizedException"/>,
    /// <see cref="ConflictException"/>) and should not be retried.
    /// </summary>
    /// <typeparam name="T">
    /// The result type of the resilience pipeline. For exception-only predicates the
    /// result type does not affect which exceptions are handled — it only needs to match
    /// the <see cref="Polly.Retry.RetryStrategyOptions{TResult}"/> type parameter.
    /// </typeparam>
    public static PredicateBuilder<T> TransientErrors<T>() =>
        new PredicateBuilder<T>()
            .Handle<TimeoutException>()
            .Handle<InvalidOperationException>();

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
