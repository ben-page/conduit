using System.Collections.Concurrent;
using Amqp;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// The sender links that carry replies on one connection, one per reply address, so a reply does not
/// attach and detach a link of its own.
/// </summary>
/// <remarks>
/// A reply address is whatever the requester put in <c>ReplyTo</c>, so the cache holds at most 64 links:
/// a new address beyond that closes the least recently used link first, however recently it was used,
/// so no number of distinct addresses opens more. Adding one also closes links unused for 5 minutes,
/// since a requester that went away leaves its link attached (Artemis keeps a link to a deleted temporary
/// address, and a send on it succeeds and goes nowhere). The next reply to a requester whose link was
/// closed either way attaches a new one.
/// </remarks>
internal sealed class ActiveMQReplySenders : IDisposable
{
    /// <summary>The most sender links cached at once.</summary>
    internal const int Capacity = 64;

    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly Session session;
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly ConcurrentDictionary<string, CachedSender> senders = new(StringComparer.Ordinal);

    // Serializes attaching a link, and the evictions that make room for it, so the cap holds
    private readonly SemaphoreSlim attaching = new(1, 1);
    private long uses;

    public ActiveMQReplySenders(Session session)
    {
        this.session = session;
    }

    /// <summary>The sender links cached now.</summary>
    internal int Count => this.senders.Count;

    /// <summary>
    /// The sender link for <paramref name="replyAddress"/>, attaching one if none is cached. Making room
    /// for a new one closes the links it evicts first, waiting for the broker's answer (at most 10 seconds
    /// each), as a management link's close does before the next attach on the session.
    /// </summary>
    public async ValueTask<SenderLink> GetAsync(string replyAddress)
    {
        if (this.TryGetOpen(replyAddress, out var link))
        {
            return link;
        }

        await this.attaching.WaitAsync();
        try
        {
            if (this.TryGetOpen(replyAddress, out link))
            {
                return link;
            }

            var now = this.timeProvider.GetTimestamp();
            var evicted = new List<SenderLink>();

            foreach (var (cachedAddress, cached) in this.senders)
            {
                if (this.timeProvider.GetElapsedTime(cached.LastUsed, now) > IdleLifetime
                    && this.senders.TryRemove(KeyValuePair.Create(cachedAddress, cached)))
                {
                    evicted.Add(cached.Link);
                }
            }

            while (this.senders.Count >= Capacity)
            {
                var (leastRecentAddress, leastRecent) = this.senders.MinBy(entry => entry.Value.Recency);
                if (this.senders.TryRemove(KeyValuePair.Create(leastRecentAddress, leastRecent)))
                {
                    evicted.Add(leastRecent.Link);
                }
            }

            await Task.WhenAll(evicted.Select(CloseQuietlyAsync));

            var created = new CachedSender(
                new SenderLink(this.session, ActiveMQTopologyManager.LinkName("reply"), replyAddress),
                now,
                Interlocked.Increment(ref this.uses));
            this.senders[replyAddress] = created;
            return created.Link;
        }
        finally
        {
            this.attaching.Release();
        }
    }

    /// <summary>Discards <paramref name="link"/>, a cached link a send failed on.</summary>
    public async Task EvictAsync(string replyAddress, SenderLink link)
    {
        if (this.senders.TryGetValue(replyAddress, out var cached)
            && ReferenceEquals(cached.Link, link)
            && this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached)))
        {
            await CloseQuietlyAsync(link);
        }
    }

    /// <inheritdoc />
    public void Dispose() => this.attaching.Dispose();

    // A cached link that is still open, marked used; a closed one is dropped
    private bool TryGetOpen(string replyAddress, out SenderLink link)
    {
        link = null!;
        if (!this.senders.TryGetValue(replyAddress, out var cached))
        {
            return false;
        }

        if (cached.Link.IsClosed)
        {
            this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached));
            return false;
        }

        cached.Touch(this.timeProvider.GetTimestamp(), Interlocked.Increment(ref this.uses));
        link = cached.Link;
        return true;
    }

    private static async Task CloseQuietlyAsync(SenderLink link)
    {
        try
        {
            if (!link.IsClosed)
            {
                await link.CloseAsync(CloseTimeout);
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Already closing or closed, or a broker that did not answer in time
        }
    }

    private sealed class CachedSender(SenderLink link, long lastUsed, long recency)
    {
        private long lastUsed = lastUsed;
        private long recency = recency;

        public SenderLink Link { get; } = link;

        /// <summary>When the link was last used, as a <see cref="TimeProvider"/> timestamp.</summary>
        public long LastUsed => Volatile.Read(ref this.lastUsed);

        /// <summary>Its place in the order of use: the smallest is the least recently used.</summary>
        public long Recency => Volatile.Read(ref this.recency);

        public void Touch(long now, long use)
        {
            Volatile.Write(ref this.lastUsed, now);
            Volatile.Write(ref this.recency, use);
        }
    }
}
