using System.Collections.Concurrent;
using Conduit.TestUtils;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// Singleton sink that records, in order, the correlation ids of every dispatch <see cref="CaptureContextHandler"/> ran.
/// </summary>
public sealed class CapturedContexts
{
    private readonly ConcurrentQueue<MessageContextSnapshot> captured = new();

    public IReadOnlyList<MessageContextSnapshot> Ids => this.captured.ToList();

    public void Add(MessageContextSnapshot ids) => this.captured.Enqueue(ids);
}
