using Conduit.Dispatch;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

public class StubInterceptor : IWireInterceptor
{
    public List<string> RequestLog { get; } = [];
    public List<string> EventLog { get; } = [];

    public async Task<object?> InterceptRequestAsync(
        DispatchEnvelope envelope,
        Func<Task<object?>> next,
        CancellationToken cancellationToken)
    {
        this.RequestLog.Add(envelope.Type);
        return await next();
    }

    public async Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken)
    {
        this.EventLog.Add(envelope.Type);
        await next();
    }
}
