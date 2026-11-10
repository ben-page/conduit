using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.SourceGen.Tests.Support;

/// <summary>
/// A transport that records the type of every envelope a generated mediator hands it, by the method it was handed to.
/// </summary>
public sealed class RecordingTransport : IConduitTransport
{
    /// <summary>The types sent with <see cref="SendRequestAsync"/>, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The types sent with <see cref="SendEventAsync"/>, in order.</summary>
    public ConcurrentQueue<string> Events { get; } = new();

    /// <inheritdoc />
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.Requests.Enqueue(envelope.Type);
        return Task.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.Events.Enqueue(envelope.Type);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken) => Task.CompletedTask;
}
