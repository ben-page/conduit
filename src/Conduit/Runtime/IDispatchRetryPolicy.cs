using Conduit.Configuration;

namespace Conduit.Runtime;

/// <summary>
/// Decides whether a failed attempt of a top-level dispatch is run again, each attempt in a fresh DI scope. Registered
/// once per host with <see cref="ConduitBuilder.AddDispatchRetry{TPolicy}"/>, as a singleton; without one, every
/// dispatch runs once on the caller's provider, as it always has. Conduit knows nothing of the failures a host retries
/// (a database deadlock, say): the policy reads its own signal from the failed attempt's services, still open when it
/// is asked.
/// </summary>
public interface IDispatchRetryPolicy
{
    /// <summary>Whether a top-level dispatch of <paramref name="requestType"/> runs under the policy.</summary>
    public bool Covers(Type requestType);

    /// <summary>
    /// Asked after a failed attempt, while its scope is still open: the reason to retry (it tags the
    /// <c>conduit.dispatch.retries</c> counter and the log), or <see langword="null"/> to stop and rethrow. Asked for the
    /// last attempt too, so a reason there counts as exhausted. Never asked for an attempt cancelled by the dispatch's
    /// own token. A policy that throws is logged and treated as <see langword="null"/>.
    /// </summary>
    public string? RetryReason(DispatchAttemptFailure failure);
}

/// <summary>A failed attempt, as <see cref="IDispatchRetryPolicy.RetryReason"/> sees it.</summary>
/// <param name="RequestType">The dispatched request's type.</param>
/// <param name="Exception">What the attempt threw.</param>
/// <param name="Attempt">The failed attempt's number, 1-based.</param>
/// <param name="MaxAttempts">The most attempts the dispatch gets (<see cref="DispatchRetryOptions.MaxAttempts"/>).</param>
/// <param name="AttemptServices">The failed attempt's scope, not yet disposed.</param>
public sealed record DispatchAttemptFailure(
    Type RequestType, Exception Exception, int Attempt, int MaxAttempts, IServiceProvider AttemptServices);
