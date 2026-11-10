using Conduit.Runtime;
using Polly.Retry;
using Polly.Telemetry;

namespace Conduit.Resilience;

/// <summary>
/// Observes each retry a Polly retry strategy makes inside a pipeline <see cref="ResilienceBehavior{TRequest,TResult}"/>
/// runs, as a <see cref="RetryAttempted"/> on the dispatch's <see cref="MessageContext"/>. It listens for the strategy's
/// <c>OnRetry</c> event, which Polly reports only once it has decided to retry a handled outcome: a failure the predicate
/// does not handle, and the last attempt of an exhausted retry, are never observed. <c>AddResilience</c> adds it to the
/// container's <see cref="TelemetryOptions"/>, which every pipeline the registry builds reports to; an execution
/// <see cref="ResilienceBehavior{TRequest,TResult}"/> did not start (a handler's own HTTP resilience, say) carries no
/// <see cref="ResilienceBehavior.DispatchKey"/> and is ignored.
/// </summary>
internal sealed class RetryObservationListener : TelemetryListener
{
    /// <summary>The one instance, so adding it twice is detectable.</summary>
    public static readonly RetryObservationListener Instance = new();

    private RetryObservationListener()
    {
    }

    /// <inheritdoc/>
    public override void Write<TResult, TArgs>(in TelemetryEventArguments<TResult, TArgs> args)
    {
        if (args.Arguments is not OnRetryArguments<TResult> retry
            || !args.Context.Properties.TryGetValue(ResilienceBehavior.DispatchKey, out var dispatched)
            || !dispatched)
        {
            return;
        }

        // AttemptNumber is Polly's zero-based count of the attempt that failed; RetryAttempted numbers it from 1, as
        // dispatch retry does. A retried result (a typed pipeline's HandleResult) has no exception: its text, no type.
        var exception = retry.Outcome.Exception;
        MessageContext.Observe(new RetryAttempted(
            retry.AttemptNumber + 1,
            exception?.Message ?? retry.Outcome.ToString(),
            exception is null ? string.Empty : exception.GetType().FullName ?? exception.GetType().Name,
            retry.RetryDelay.TotalMilliseconds));
    }
}
