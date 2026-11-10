using System.Collections.Concurrent;
using Amqp;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// The sender links that carry replies on one connection, one per reply address, so a reply does not
/// attach and detach a link of its own.
/// </summary>
/// <remarks>
/// <para>
/// A reply address is whatever the requester put in <c>ReplyTo</c>, so the cache holds at most 64 links.
/// A new address beyond that evicts the least recently used link that no send is using, and adding one
/// also evicts links unused for 5 minutes, since a requester that went away leaves its link attached
/// (Artemis keeps a link to a deleted temporary address, and a send on it succeeds and goes nowhere).
/// When every cached link has a send in flight, the reply gets a link of its own, closed after its send.
/// </para>
/// <para>
/// A link is never closed under a send: each reply holds a <see cref="Lease"/> for the length of its
/// send, eviction claims only a link no lease holds, and a link discarded after a failed send closes once
/// the last lease on it is released. A failed reply is never retried (the base class acknowledges the
/// request), so closing a link under a send would lose that reply for good. Evicted links are closed after
/// the attach lock is released, without waiting, so a broker slow to answer a detach holds no other reply.
/// </para>
/// </remarks>
internal sealed class ActiveMQReplySenders
{
    /// <summary>The most sender links cached at once.</summary>
    internal const int Capacity = 64;

    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly Session session;
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly ConcurrentDictionary<string, CachedSender> senders = new(StringComparer.Ordinal);

    // Serializes attaching a cached link and the evictions that make room for it, so the cap holds
    private readonly Lock attaching = new();
    private long uses;

    public ActiveMQReplySenders(Session session)
    {
        this.session = session;
    }

    /// <summary>The sender links cached now.</summary>
    internal int Count => this.senders.Count;

    /// <summary>
    /// A sender link for one reply to <paramref name="replyAddress"/>, held until the lease is released:
    /// the cached one, a newly attached and cached one, or, when every cached link has a send in flight,
    /// one for this reply alone.
    /// </summary>
    public Lease Acquire(string replyAddress)
    {
        if (this.TryLeaseCached(replyAddress) is { } leased)
        {
            return leased;
        }

        List<CachedSender> evicted = [];
        try
        {
            lock (this.attaching)
            {
                if (this.TryLeaseCached(replyAddress) is { } leasedMeanwhile)
                {
                    return leasedMeanwhile;
                }

                var now = this.timeProvider.GetTimestamp();
                foreach (var (cachedAddress, cached) in this.senders)
                {
                    if (this.timeProvider.GetElapsedTime(cached.LastUsed, now) > IdleLifetime)
                    {
                        this.TryEvict(cachedAddress, cached, evicted);
                    }
                }

                while (this.senders.Count >= Capacity)
                {
                    var claimed = this.senders
                        .OrderBy(entry => entry.Value.Recency)
                        .Any(entry => this.TryEvict(entry.Key, entry.Value, evicted));

                    if (!claimed)
                    {
                        // Every cached link has a send in flight: none is closed under it
                        return new Lease(this, replyAddress, cached: null, this.Attach(replyAddress));
                    }
                }

                var created = new CachedSender(this.Attach(replyAddress), now, Interlocked.Increment(ref this.uses));
                created.TryAcquire();
                this.senders[replyAddress] = created;
                return new Lease(this, replyAddress, created, created.Link);
            }
        }
        finally
        {
            // After the lock is released: a broker slow to answer a detach holds no other reply's attach
            foreach (var cached in evicted)
            {
                cached.CloseOnce();
            }
        }
    }

    private SenderLink Attach(string replyAddress) =>
        new(this.session, ActiveMQTopologyManager.LinkName("reply"), replyAddress);

    // A lease on the cached, open link that no eviction has claimed, marking it used; null if there is none
    private Lease? TryLeaseCached(string replyAddress)
    {
        if (!this.senders.TryGetValue(replyAddress, out var cached))
        {
            return null;
        }

        if (cached.Link.IsClosed)
        {
            // The broker closed it: the next reply attaches a new one
            this.Discard(replyAddress, cached);
            return null;
        }

        if (!cached.TryAcquire())
        {
            return null;
        }

        cached.Touch(this.timeProvider.GetTimestamp(), Interlocked.Increment(ref this.uses));
        return new Lease(this, replyAddress, cached, cached.Link);
    }

    // Removes a link no lease holds, to be closed once the attach lock is released
    private bool TryEvict(string replyAddress, CachedSender cached, List<CachedSender> evicted)
    {
        if (!cached.TryClaimIdle())
        {
            return false;
        }

        this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached));
        evicted.Add(cached);
        return true;
    }

    // Takes a link out of the cache; it closes once the last lease on it is released
    private void Discard(string replyAddress, CachedSender cached)
    {
        this.senders.TryRemove(KeyValuePair.Create(replyAddress, cached));
        cached.MarkEvicting();
    }

    // Detaches, waiting at most 10 seconds for the broker's answer; never throws
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

    /// <summary>
    /// A sender link held for one reply's send. Release it, in a <c>finally</c>, when the send is over;
    /// call <see cref="Discard"/> first if the send failed, so the next reply gets a fresh link.
    /// </summary>
    internal sealed class Lease
    {
        private readonly ActiveMQReplySenders owner;
        private readonly string replyAddress;
        private readonly CachedSender? cached;
        private int released;

        internal Lease(ActiveMQReplySenders owner, string replyAddress, CachedSender? cached, SenderLink link)
        {
            this.owner = owner;
            this.replyAddress = replyAddress;
            this.cached = cached;
            this.Link = link;
        }

        /// <summary>The link to send the reply on.</summary>
        public SenderLink Link { get; }

        /// <summary>Whether the link is a cached one, rather than one for this reply alone.</summary>
        internal bool IsCached => this.cached is not null;

        /// <summary>
        /// Takes a cached link a send failed on out of the cache; other replies still sending on it
        /// finish first, and it closes when the last of them is done.
        /// </summary>
        public void Discard()
        {
            if (this.cached is not null)
            {
                this.owner.Discard(this.replyAddress, this.cached);
            }
        }

        /// <summary>Ends the hold, once: an uncached link is closed, a cached one stays for the next reply.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 1)
            {
                return;
            }

            if (this.cached is null)
            {
                _ = CloseQuietlyAsync(this.Link);
            }
            else
            {
                this.cached.Release();
            }
        }
    }

    /// <summary>
    /// A cached link with the sends holding it and its eviction state. Acquiring increments the count
    /// and then reads the state; claiming sets the state and then reads the count; both through full
    /// fences, so one of them always sees the other and no link is claimed with a send on it.
    /// </summary>
    internal sealed class CachedSender(SenderLink link, long lastUsed, long recency)
    {
        private const int Open = 0;
        private const int Claiming = 1;
        private const int Evicting = 2;

        private long lastUsed = lastUsed;
        private long recency = recency;
        private int inFlight;
        private int state = Open;
        private int closing;

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

        /// <summary>Holds the link for a send, unless an eviction has claimed it.</summary>
        public bool TryAcquire()
        {
            Interlocked.Increment(ref this.inFlight);
            if (Volatile.Read(ref this.state) == Open)
            {
                return true;
            }

            this.Release();
            return false;
        }

        /// <summary>Ends a send's hold; the last one on an evicted link closes it.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref this.inFlight) == 0 && Volatile.Read(ref this.state) == Evicting)
            {
                this.CloseOnce();
            }
        }

        /// <summary>Claims the link for eviction, only while no send holds it.</summary>
        public bool TryClaimIdle()
        {
            if (Interlocked.CompareExchange(ref this.state, Claiming, Open) != Open)
            {
                return false;
            }

            if (Volatile.Read(ref this.inFlight) == 0)
            {
                // Evicting, or already so through a discard
                Interlocked.CompareExchange(ref this.state, Evicting, Claiming);
                return true;
            }

            // A send took it meanwhile: keep it (a discard meanwhile keeps it evicting)
            Interlocked.CompareExchange(ref this.state, Open, Claiming);
            return false;
        }

        /// <summary>Marks the link evicted whatever holds it; it closes when no send holds it any more.</summary>
        public void MarkEvicting()
        {
            Interlocked.Exchange(ref this.state, Evicting);
            if (Volatile.Read(ref this.inFlight) == 0)
            {
                this.CloseOnce();
            }
        }

        /// <summary>Starts closing the link, once, without waiting for it.</summary>
        public void CloseOnce()
        {
            if (Interlocked.Exchange(ref this.closing, 1) == 0)
            {
                _ = CloseQuietlyAsync(this.Link);
            }
        }
    }
}
