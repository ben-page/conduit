using System.Collections.Concurrent;
using Conduit.TestUtils;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Singleton sink that records, in order, the context of every dispatch <see cref="CaptureContextHandler"/> ran.
/// </summary>
public sealed class CapturedContexts
{
    private readonly ConcurrentQueue<(MessageContextSnapshot Ids, IReadOnlyDictionary<string, object?>? Properties)> captured = new();

    public IReadOnlyList<MessageContextSnapshot> Ids => this.captured.Select(entry => entry.Ids).ToList();

    public IReadOnlyList<IReadOnlyDictionary<string, object?>?> Properties =>
        this.captured.Select(entry => entry.Properties).ToList();

    public void Add(MessageContextSnapshot ids, IReadOnlyDictionary<string, object?>? properties) =>
        this.captured.Enqueue((ids, properties));
}
