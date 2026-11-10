using System.Collections.Concurrent;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>Singleton sink for the <c>Authorization</c> header of every request a test host received.</summary>
public sealed class RecordedAuthorizationHeaders
{
    private readonly ConcurrentQueue<string> received = new();

    public IReadOnlyList<string> Received => this.received.ToList();

    public void Add(string header) => this.received.Enqueue(header);
}
