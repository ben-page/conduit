using System.Runtime.ExceptionServices;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// The attempt loop behind <see cref="ConduitRuntime.DispatchAttemptsAsync{TRequest, TResult}"/>, registered as a
/// singleton by <see cref="ConduitBuilder.AddDispatchRetry{TPolicy}"/>; its presence in DI is what turns retry on.
/// Holds no per-dispatch state: each dispatch's attempt is flow state (<see cref="DispatchAttempt.Current"/>).
/// </summary>
internal sealed class DispatchRetryRunner
{
    private readonly DispatchRetryOptions options;
    private readonly ILogger<DispatchRetryRunner> logger;

    public DispatchRetryRunner(
        IDispatchRetryPolicy policy,
        DispatchRetryOptions options,
        ILogger<DispatchRetryRunner> logger)
    {
        this.Policy = policy;
        this.options = options;
        this.logger = logger;
    }

    public IDispatchRetryPolicy Policy { get; }

    /// <summary>
    /// Runs <paramref name="attempt"/> up to <see cref="DispatchRetryOptions.MaxAttempts"/> times, each in a fresh scope
    /// from the root factory, until it succeeds or the policy gives no reason to go on. The scope is disposed before
    /// the wait, so a retry holds nothing of the failed attempt while it waits.
    /// </summary>
    public async Task<TResult> RunAsync<TRequest, TResult>(
        IServiceProvider services,
        Func<IServiceProvider, Task<TResult>> attempt,
        CancellationToken cancellationToken)
    {
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var requestType = typeof(TRequest);
        var maxAttempts = this.options.MaxAttempts;

        for (var number = 1; ; number++)
        {
            ExceptionDispatchInfo? answer = null;
            var delay = TimeSpan.Zero;

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var previous = DispatchAttempt.Current;
                var current = new DispatchAttempt(number, maxAttempts, scope.ServiceProvider);
                DispatchAttempt.SetCurrent(current);
                try
                {
                    var result = await attempt(scope.ServiceProvider);
                    current.Keep();
                    if (number > 1)
                    {
                        ConduitRuntime.LogDispatchRetrySucceeded(this.logger, TypeName(requestType), number);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller gave up: the answer, never a failure to offer the policy.
                    current.Keep();
                    throw;
                }
#pragma warning disable CA1031 // Not swallowed: offered to the policy, then rethrown unless it is retried.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    // Asked here, inside the await using, so the policy reads the failed attempt's own services.
                    var reason = this.AskPolicy(new DispatchAttemptFailure(
                        requestType, exception, number, maxAttempts, scope.ServiceProvider));

                    if (reason is null || number >= maxAttempts)
                    {
                        current.Keep();
                        if (reason is not null)
                        {
                            ConduitTelemetry.RecordDispatchRetry(TypeName(requestType), reason, exhausted: true);
                        }

                        answer = ExceptionDispatchInfo.Capture(exception);
                    }
                    else
                    {
                        current.Abandon();
                        delay = this.NextDelay(number);
                        ConduitTelemetry.RecordDispatchRetry(TypeName(requestType), reason, exhausted: false);

                        // The loop runs on the dispatch's own context, so its one record shows each retry.
                        MessageContext.Observe(new RetryAttempted(
                            number,
                            exception.Message,
                            exception.GetType().FullName ?? exception.GetType().Name,
                            delay.TotalMilliseconds));
                        ConduitRuntime.LogDispatchRetrying(
                            this.logger, TypeName(requestType), number, maxAttempts, reason, delay.TotalMilliseconds);
                    }
                }
                finally
                {
                    DispatchAttempt.SetCurrent(previous);
                }
            }

            answer?.Throw();

            await Task.Delay(delay, this.options.TimeProvider, cancellationToken);
        }
    }

    private string? AskPolicy(DispatchAttemptFailure failure)
    {
        try
        {
            return this.Policy.RetryReason(failure);
        }
#pragma warning disable CA1031 // A broken policy must not replace the attempt's own failure: it stops the loop.
        catch (Exception policyException)
#pragma warning restore CA1031
        {
            ConduitRuntime.LogDispatchRetryPolicyFailed(
                this.logger, this.Policy.GetType().Name, TypeName(failure.RequestType), policyException);
            return null;
        }
    }

    /// <summary>The wait before attempt <paramref name="failedAttempt"/> + 1: jittered within its range.</summary>
    private TimeSpan NextDelay(int failedAttempt)
    {
        var range = this.options.Delays[Math.Min(failedAttempt - 1, this.options.Delays.Count - 1)];
#pragma warning disable CA5394 // Jitter that spreads retries apart, not a secret.
        return range.Min + ((range.Max - range.Min) * Random.Shared.NextDouble());
#pragma warning restore CA5394
    }

    private static string TypeName(Type requestType) => requestType.FullName ?? requestType.Name;
}
