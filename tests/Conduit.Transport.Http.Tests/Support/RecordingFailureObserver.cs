using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>An <see cref="IInboundFailureObserver"/> that records every failure it is told of, as (type, error).</summary>
public sealed class RecordingFailureObserver : IInboundFailureObserver
{
    private readonly ConcurrentQueue<(string MessageType, DispatchError Error)> failures = new();

    public IReadOnlyList<(string MessageType, DispatchError Error)> Failures => [.. this.failures];

    public ValueTask OnDispatchFailedAsync(string messageType, DispatchError error, CancellationToken cancellationToken)
    {
        this.failures.Enqueue((messageType, error));
        return ValueTask.CompletedTask;
    }
}
