using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Conduit.Runtime;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Observes dispatch pipeline events and consolidates them into <see cref="DispatchAuditRecord"/>
/// instances. Groups observations by message ID and seals the group when a terminal observation
/// (<see cref="DispatchCompleted"/> or <see cref="DispatchFailed"/>) is received.
/// Sealed records are written to a channel for downstream processing.
/// </summary>
public sealed class AuditObserver : IConduitObserver
{
    private static readonly string AuditBatchTypeName = typeof(AuditBatch).FullName!;

    private readonly ConcurrentDictionary<string, DispatchGroup> activeGroups = new();

    // The groups the sweep evicted, keyed weakly by their dispatch's context: every observation of a dispatch carries
    // the context its DispatchInvoked did (MessageContext.Observe passes Current), so a dispatch still running finds its
    // group here, and the group goes when the context does, whether or not the dispatch ever ends.
    private readonly ConditionalWeakTable<MessageContext, DispatchGroup> evictedGroups = new();
    private readonly ChannelWriter<DispatchAuditRecord> channel;
    private readonly string serviceName;
    private readonly IReadOnlySet<string> suppressedTypes;
    private readonly IReadOnlySet<string> suppressedPayloadTypes;
    private readonly int maxPayloadBytes;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Creates a new audit observer that writes sealed records to the specified channel.
    /// </summary>
    /// <param name="channel">The channel writer for sealed audit records.</param>
    /// <param name="serviceName">The service name to include in audit records.</param>
    public AuditObserver(ChannelWriter<DispatchAuditRecord> channel, string serviceName)
        : this(channel, serviceName, new HashSet<string>(), new HashSet<string>())
    {
    }

    /// <summary>
    /// Creates a new audit observer that writes sealed records to the specified channel,
    /// honouring per-type suppression.
    /// </summary>
    /// <param name="channel">The channel writer for sealed audit records.</param>
    /// <param name="serviceName">The service name to include in audit records.</param>
    /// <param name="suppressedTypes">Full type names for which no audit record is recorded at all.</param>
    /// <param name="suppressedPayloadTypes">Full type names for which the payload body is omitted from the record.</param>
    public AuditObserver(
        ChannelWriter<DispatchAuditRecord> channel,
        string serviceName,
        IReadOnlySet<string> suppressedTypes,
        IReadOnlySet<string> suppressedPayloadTypes)
        : this(channel, serviceName, suppressedTypes, suppressedPayloadTypes, Configuration.AuditOptions.DefaultMaxPayloadBytes)
    {
    }

    /// <summary>
    /// Creates a new audit observer that writes sealed records to the specified channel, honouring per-type suppression
    /// and capping each record's payload at <paramref name="maxPayloadBytes"/>.
    /// </summary>
    /// <param name="channel">The channel writer for sealed audit records.</param>
    /// <param name="serviceName">The service name to include in audit records.</param>
    /// <param name="suppressedTypes">Full type names for which no audit record is recorded at all.</param>
    /// <param name="suppressedPayloadTypes">Full type names for which the payload body is omitted from the record.</param>
    /// <param name="maxPayloadBytes">The largest body, in UTF-8 bytes, a sealed record keeps (0 or less: no cap); see
    /// <see cref="Configuration.AuditOptions.MaxPayloadBytes"/>. The other constructors use the 64 KB default.</param>
    /// <param name="timeProvider">The clock a dispatch's record is stamped with (its <see cref="DispatchAuditRecord.Timestamp"/>)
    /// and each observation is timed by (the inactivity <see cref="EvictStaleGroups"/> measures); null, as the other
    /// constructors pass, is <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPayloadBytes"/> is above 0 but below
    /// <see cref="Configuration.AuditOptions.MinimumMaxPayloadBytes"/>.</exception>
    public AuditObserver(
        ChannelWriter<DispatchAuditRecord> channel,
        string serviceName,
        IReadOnlySet<string> suppressedTypes,
        IReadOnlySet<string> suppressedPayloadTypes,
        int maxPayloadBytes,
        TimeProvider? timeProvider = null)
    {
        AuditPayloadCap.ThrowIfInvalid(maxPayloadBytes, nameof(maxPayloadBytes));
        ArgumentNullException.ThrowIfNull(suppressedTypes);
        ArgumentNullException.ThrowIfNull(suppressedPayloadTypes);

        this.channel = channel;
        this.serviceName = serviceName;
        this.suppressedTypes = suppressedTypes;
        this.suppressedPayloadTypes = suppressedPayloadTypes;
        this.maxPayloadBytes = maxPayloadBytes;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void OnObservation(MessageObservation observation, MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        // Skip observations for AuditBatch events, and for explicitly suppressed types, to
        // prevent feedback loops (AuditBatch) and to honour Suppress<T>(). For suppressed types
        // we never create a group, so subsequent non-initial observations fall through the
        // untracked-message-id guard below and are dropped too.
        if (observation is DispatchInvoked invoked
            && (invoked.MessageType == AuditBatchTypeName
                || this.suppressedTypes.Contains(invoked.MessageType)))
        {
            return;
        }

        // Only DispatchInvoked (always the first observation) opens a group. A later observation finds its group among
        // the active ones, or among those the sweep evicted (written provisionally, still open); with neither, its
        // DispatchInvoked was skipped above, and it is dropped. An active group under this message ID is this dispatch's
        // only if it was opened from this context: once the sweep has moved a dispatch's group out, the same message
        // delivered again (no inbox) opens a group of its own under the ID, and the first dispatch's later observations
        // belong in its evicted group, not in that one.
        DispatchGroup? group = null;
        if (observation is not DispatchInvoked
            && !(this.activeGroups.TryGetValue(context.MessageId, out group) && ReferenceEquals(group.Context, context))
            && !this.evictedGroups.TryGetValue(context, out group))
        {
            return;
        }

        var observedAt = this.timeProvider.GetUtcNow().UtcDateTime;
        group ??= this.activeGroups.GetOrAdd(
            context.MessageId,
            _ => new DispatchGroup(context, this.serviceName, this.maxPayloadBytes, observedAt));

        // If this dispatch's type opts out of payload capture, drop the body now that we know
        // the type (carried by DispatchInvoked, which is always the first observation).
        if (observation is DispatchInvoked payloadCheck
            && this.suppressedPayloadTypes.Contains(payloadCheck.MessageType))
        {
            group.SuppressPayload();
        }

        // False only once the group has sealed: an observation after the dispatch's terminal one.
        if (!group.Add(observation, observedAt) || observation is not (DispatchCompleted or DispatchFailed))
        {
            return;
        }

        // Wherever the group is, it is finished. Seal now, while the payload is readable; only the write waits. The weak
        // table (one internal lock) is touched only for a group the sweep moved there: a snapshot either happened before
        // this terminal observation was added, and IsEvicted shows it, or cannot happen after it, the group having just
        // observed.
        this.activeGroups.TryRemove(new KeyValuePair<string, DispatchGroup>(context.MessageId, group));
        if (group.IsEvicted)
        {
            this.evictedGroups.Remove(context);
        }

        this.Write(group.Seal(), context);
    }

    /// <summary>
    /// Writes a provisional record of each dispatch that has made no observation for <paramref name="staleTimeout"/>
    /// before <paramref name="now"/>. An incomplete dispatch (no terminal observation) would otherwise hold its group
    /// forever, and its trail would never reach the store. The age is measured from a group's last observation, not its
    /// first. The provisional record holds the observations so far and the payload, and ends in a non-terminal marker
    /// (a <see cref="CustomObservation"/> named <c>Conduit.Audit.StaleGroupEviction</c>): it records no outcome. The
    /// group leaves the active set but stays open, held weakly by its dispatch's context, so when the dispatch does end,
    /// its real record is written with the same <see cref="DispatchAuditRecord.MessageId"/>, the later of the two. A
    /// group is written provisionally once at most; one whose dispatch never ends goes with its context.
    /// </summary>
    /// <param name="now">The current time used to evaluate inactivity. Injected for deterministic testing.</param>
    /// <param name="staleTimeout">The inactivity beyond which an incomplete group is written provisionally.</param>
    public void EvictStaleGroups(DateTime now, TimeSpan staleTimeout)
    {
        foreach (var entry in this.activeGroups)
        {
            var group = entry.Value;
            if (!group.SnapshotIfInactive(now, staleTimeout, provisional => this.Write(provisional, group.Context)))
            {
                continue;
            }

            // Moved before it leaves the active set, so an observation that no longer finds it there finds it here.
            this.evictedGroups.AddOrUpdate(group.Context, group);
            this.activeGroups.TryRemove(entry);
        }
    }

    // A dispatch nested in a retrying attempt is written once that attempt is kept and dropped if it is abandoned, its
    // provisional record as its real one; OnKept keeps their order.
    private void Write(DispatchAuditRecord record, MessageContext context)
    {
        if (context.Attempt is { } attempt)
        {
            attempt.OnKept(() => this.channel.TryWrite(record));
        }
        else
        {
            this.channel.TryWrite(record);
        }
    }
}
