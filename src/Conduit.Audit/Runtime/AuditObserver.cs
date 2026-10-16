using System.Collections.Concurrent;
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
    private readonly ChannelWriter<DispatchAuditRecord> channel;
    private readonly string serviceName;
    private readonly IReadOnlySet<string> suppressedTypes;
    private readonly IReadOnlySet<string> suppressedPayloadTypes;

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
    {
        ArgumentNullException.ThrowIfNull(suppressedTypes);
        ArgumentNullException.ThrowIfNull(suppressedPayloadTypes);

        this.channel = channel;
        this.serviceName = serviceName;
        this.suppressedTypes = suppressedTypes;
        this.suppressedPayloadTypes = suppressedPayloadTypes;
    }

    /// <inheritdoc />
    public void OnObservation(MessageObservation observation, MessageContext context)
    {
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

        // Also skip non-initial observations for message IDs we never started tracking
        // (because we skipped their DispatchInvoked above).
        if (observation is not DispatchInvoked && !this.activeGroups.ContainsKey(context.MessageId))
        {
            return;
        }

        var group = this.activeGroups.GetOrAdd(
            context.MessageId,
            _ => new DispatchGroup(context, this.serviceName));

        // If this dispatch's type opts out of payload capture, drop the body now that we know
        // the type (carried by DispatchInvoked, which is always the first observation).
        if (observation is DispatchInvoked payloadCheck
            && this.suppressedPayloadTypes.Contains(payloadCheck.MessageType))
        {
            group.SuppressPayload();
        }

        group.Add(observation);

        if (observation is DispatchCompleted or DispatchFailed)
        {
            if (this.activeGroups.TryRemove(context.MessageId, out var completed))
            {
                this.channel.TryWrite(completed.Seal());
            }
        }
    }

    /// <summary>
    /// Evicts dispatch groups whose creation time is older than <paramref name="staleTimeout"/>
    /// relative to <paramref name="now"/>. Incomplete dispatches (no terminal observation) would
    /// otherwise leak forever in <c>activeGroups</c>. Each evicted group is sealed and flushed as
    /// an incomplete/failed record rather than silently dropped, preserving the partial audit trail.
    /// </summary>
    /// <param name="now">The current time used to evaluate group age. Injected for deterministic testing.</param>
    /// <param name="staleTimeout">The age beyond which an incomplete group is considered stale.</param>
    public void EvictStaleGroups(DateTime now, TimeSpan staleTimeout)
    {
        foreach (var entry in this.activeGroups)
        {
            if (now - entry.Value.CreatedAt < staleTimeout)
            {
                continue;
            }

            if (this.activeGroups.TryRemove(entry.Key, out var stale))
            {
                stale.MarkIncomplete(staleTimeout);
                this.channel.TryWrite(stale.Seal());
            }
        }
    }
}
