using System.Collections.Concurrent;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// Singleton sink for <see cref="ContextRecordingHub"/>: the <c>context</c> argument of every <c>Dispatch</c> it received,
/// exactly as it arrived (null when the caller sent none).
/// </summary>
public sealed class RecordedHubContexts
{
    private readonly ConcurrentQueue<Dictionary<string, string>?> received = new();

    public IReadOnlyList<Dictionary<string, string>?> Received => this.received.ToList();

    public void Add(Dictionary<string, string>? context) => this.received.Enqueue(context);
}
