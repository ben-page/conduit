using System.Collections.Concurrent;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// An <see cref="IInboundErrorInterceptor"/> that takes every failure it is offered, recording the message
/// type, support reference and exception, as the dead letter queue would store them.
/// </summary>
public sealed class RecordingErrorInterceptor : IInboundErrorInterceptor
{
    private readonly ConcurrentQueue<(string MessageType, string? Reference, Exception Exception)> captures = new();

    /// <summary>The failures captured, in order.</summary>
    public IReadOnlyList<(string MessageType, string? Reference, Exception Exception)> Captures =>
        this.captures.ToArray();

    public Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        string? reference,
        CancellationToken cancellationToken)
    {
        this.captures.Enqueue((messageType, reference, exception));
        return Task.FromResult(true);
    }
}
