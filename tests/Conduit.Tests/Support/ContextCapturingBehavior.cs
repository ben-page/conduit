using Conduit.Dispatch;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// Holds captured MessageContexts. Non-generic to avoid CA1000.
/// </summary>
public static class CapturedContext
{
    private static readonly List<(string RequestType, MessageContext Context)> Entries = [];

    public static MessageContext? Last => Entries.Count > 0 ? Entries[^1].Context : null;

    public static IReadOnlyList<(string RequestType, MessageContext Context)> All => Entries;

    public static MessageContext? ForType(string typeName) =>
        Entries.Find(e => e.RequestType == typeName).Context;

    public static void Reset() => Entries.Clear();

    internal static void Capture<TRequest>(MessageContext context) =>
        Entries.Add((typeof(TRequest).FullName!, context));
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
