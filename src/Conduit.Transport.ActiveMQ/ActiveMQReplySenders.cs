using System.Collections.Concurrent;
using Amqp;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// The sender links that carry replies on one connection, one per reply address, so a reply does not
/// attach and detach a link of its own.
/// </summary>
/// <remarks>
/// A reply address is a requester connection's temporary queue, so over a long run the addresses keep
/// changing: when a requester goes away, Artemis leaves a link to its address attached (a send to it
/// still succeeds, and goes nowhere). Once more than 64 links are cached, adding one closes those unused
/// for 5 minutes; the next reply to a requester whose link was closed that way attaches a new one.
/// </remarks>
internal sealed class ActiveMQReplySenders
{
    private const int SweepThreshold = 64;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(5);

    private readonly Session session;
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly ConcurrentDictionary<string, CachedSender> senders = new(StringComparer.Ordinal);

    public ActiveMQReplySenders(Session session)
    {
        this.session = session;
    }

    /// <summary>The sender link for <paramref name="replyAddress"/>, attaching one if none is cached.</summary>
    public SenderLink Get(string replyAddress)
    {
        var now = this.timeProvider.GetTimestamp();
        while (true)
        {
            if (this.senders.TryGetValue(replyAddress, out var cached))
            {
                if (!cached.Link.IsClosed)
                {
                    cached.Touch(now);
                    return cached.Link;
                }

                this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached));
                continue;
            }

            var created = new CachedSender(
                new SenderLink(this.session, ActiveMQTopologyManager.LinkName("reply"), replyAddress), now);
            if (this.senders.TryAdd(replyAddress, created))
            {
                this.SweepIdle(now);
                return created.Link;
            }

            // Another reply attached one for the same address first
            CloseQuietly(created.Link);
        }
    }

    /// <summary>Discards <paramref name="link"/>, a cached link a send failed on.</summary>
    public void Evict(string replyAddress, SenderLink link)
    {
        if (this.senders.TryGetValue(replyAddress, out var cached)
            && ReferenceEquals(cached.Link, link)
            && this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached)))
        {
            CloseQuietly(link);
        }
    }

    private void SweepIdle(long now)
    {
        if (this.senders.Count <= SweepThreshold)
        {
            return;
        }

        foreach (var (replyAddress, cached) in this.senders)
        {
            if (this.timeProvider.GetElapsedTime(cached.LastUsed, now) > IdleLifetime
                && this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached)))
            {
                CloseQuietly(cached.Link);
            }
        }
    }

    // Detaches without waiting for the broker's answer
    private static void CloseQuietly(SenderLink link)
    {
        try
        {
            link.Close(TimeSpan.Zero);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Already closing or closed
        }
    }

    private sealed class CachedSender(SenderLink link, long lastUsed)
    {
        private long lastUsed = lastUsed;

        public SenderLink Link { get; } = link;

        public long LastUsed => Volatile.Read(ref this.lastUsed);

        public void Touch(long now) => Volatile.Write(ref this.lastUsed, now);
    }
}
