using System.Collections.Concurrent;
using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// An in-memory stand-in for the durable inbox: a gate that discards a message whose
/// <c>MessageContext.Current.MessageId</c> it has already seen and lets a dispatch with no context through, keyed
/// exactly as <c>InboxInterceptor</c> keys its dedup. It records every id it saw.
/// </summary>
public sealed class MessageIdDedupGate : IInboundInterceptor
{
    private readonly ConcurrentDictionary<string, bool> seen = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Seen => this.seen.Keys.ToList();

    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        var messageId = MessageContext.Current?.MessageId;
        return Task.FromResult(messageId is null || this.seen.TryAdd(messageId, true));
    }
}
