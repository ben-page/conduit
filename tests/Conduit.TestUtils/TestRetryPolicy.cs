using Conduit.Runtime;

namespace Conduit.TestUtils;

/// <summary>
/// A dispatch retry policy for tests: it covers every request type and retries a <see cref="RetryMeException"/>,
/// with the reason <see cref="Reason"/>. Any other failure is the dispatch's answer.
/// </summary>
public sealed class TestRetryPolicy : IDispatchRetryPolicy
{
    /// <summary>The retry reason this policy gives.</summary>
    public const string Reason = "retry-me";

    public bool Covers(Type requestType) => true;

    public string? RetryReason(DispatchAttemptFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Exception is RetryMeException ? Reason : null;
    }
}
