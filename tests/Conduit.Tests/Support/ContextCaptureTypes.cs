using System.Collections.Concurrent;
using Conduit.Runtime;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// Holds captured MessageContexts. Non-generic to avoid CA1000.
/// </summary>
public static class CapturedContext
{
    private static readonly ConcurrentQueue<(string RequestType, MessageContext Context)> Entries = new();

    public static MessageContext? Last =>
        Entries.TryPeek(out _) ? Entries.ToArray()[^1].Context : null;

    public static IReadOnlyList<(string RequestType, MessageContext Context)> All =>
        Entries.ToArray();

    public static MessageContext? ForType(string typeName)
    {
        foreach (var entry in Entries)
        {
            if (entry.RequestType == typeName)
            {
                return entry.Context;
            }
        }

        return null;
    }

    public static void Reset() => Entries.Clear();

    internal static void Capture<TRequest>(MessageContext context) =>
        Entries.Enqueue((typeof(TRequest).FullName!, context));
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
        var context = MessageContext.Current;
        if (context is not null)
        {
            CapturedContext.Capture<TRequest>(context);
        }

        return await next();
    }
}
