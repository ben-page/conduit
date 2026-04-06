using Conduit.Exceptions;
using Polly;

namespace Conduit.Resilience;

/// <summary>
/// Pre-built Polly v8 predicates that understand Conduit's exception hierarchy.
/// Each property returns a fresh <see cref="PredicateBuilder{T}"/> instance so callers
/// can safely chain additional <c>.Handle&lt;T&gt;()</c> calls without mutating a shared instance.
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
    public static PredicateBuilder<object> TransientErrors =>
        new PredicateBuilder<object>()
            .Handle<TimeoutException>()
            .Handle<InvalidOperationException>();

    /// <summary>
    /// Handles all exceptions except <see cref="OperationCanceledException"/>.
    /// Use when you want to retry on any failure that isn't an explicit cancellation.
    /// </summary>
    public static PredicateBuilder<object> AllErrors =>
        new PredicateBuilder<object>()
            .Handle<Exception>(ex => ex is not OperationCanceledException);
}
