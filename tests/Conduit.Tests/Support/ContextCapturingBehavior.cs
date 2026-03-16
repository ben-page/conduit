using Conduit.Dispatch;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// Holds the last captured MessageContext. Non-generic to avoid CA1000.
/// </summary>
public static class CapturedContext
{
    public static MessageContext? Last { get; set; }

    public static void Reset() => Last = null;
}

/// <summary>
/// Pipeline behavior that captures MessageContext during execution for test assertions.
/// </summary>
public sealed class ContextCapturingBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        CapturedContext.Last = MessageContext.Current;
        return await next();
    }
}
