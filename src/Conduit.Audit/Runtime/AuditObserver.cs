using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Observes dispatch pipeline events and consolidates them into <see cref="DispatchAuditRecord"/>
/// instances. Groups observations by message ID and seals the group when a terminal observation
/// (<see cref="DispatchCompleted"/> or <see cref="DispatchFailed"/>) is received.
/// Sealed records are written to a channel for downstream processing.
/// </summary>
public sealed partial class AuditObserver : IConduitObserver
{
    private static readonly string AuditBatchTypeName = typeof(AuditBatch).FullName!;
    private static readonly object EvictedMarker = new();

    private readonly ConcurrentDictionary<string, DispatchGroup> activeGroups = new();

    // The contexts of dispatches the sweep evicted, held weakly: a terminal observation that arrives later is recognised
    // for as long as its dispatch runs, and no entry outlives the dispatch's context.
    private readonly ConditionalWeakTable<MessageContext, object> evictedContexts = new();
    private readonly ChannelWriter<DispatchAuditRecord> channel;
    private readonly string serviceName;
    private readonly IReadOnlySet<string> suppressedTypes;
    private readonly IReadOnlySet<string> suppressedPayloadTypes;
    private readonly int maxPayloadBytes;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

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
    /// <param name="logger">Where a terminal observation arriving after its group was evicted is noted (Debug); null, as
    /// the other constructors pass, logs nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPayloadBytes"/> is above 0 but below
    /// <see cref="Configuration.AuditOptions.MinimumMaxPayloadBytes"/>.</exception>
    public AuditObserver(
        ChannelWriter<DispatchAuditRecord> channel,
        string serviceName,
        IReadOnlySet<string> suppressedTypes,
        IReadOnlySet<string> suppressedPayloadTypes,
        int maxPayloadBytes,
        TimeProvider? timeProvider = null,
        ILogger<AuditObserver>? logger = null)
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
        this.logger = logger ?? (ILogger)NullLogger.Instance;
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

        // Only DispatchInvoked (always the first observation) opens a group. A later observation for a message ID with
        // none is dropped: its DispatchInvoked was skipped above, or the sweep evicted its group.
        DispatchGroup? group = null;
        if (observation is not DispatchInvoked && !this.activeGroups.TryGetValue(context.MessageId, out group))
        {
            if (IsTerminal(observation) && this.evictedContexts.TryGetValue(context, out _))
            {
                LogTerminalAfterEviction(this.logger, context.MessageId, observation.GetType().Name);
            }

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

        if (!group.Add(observation, observedAt))
        {
            // Sealed since the lookup: the sweep evicted it in between.
            if (IsTerminal(observation) && group.IsEvicted)
            {
                LogTerminalAfterEviction(this.logger, context.MessageId, observation.GetType().Name);
            }

            return;
        }

        if (IsTerminal(observation))
        {
            if (this.activeGroups.TryRemove(context.MessageId, out var completed))
            {
                // Seal now, while the payload is readable; only the write waits. A dispatch nested in a retrying
                // attempt is written once that attempt is kept and dropped if it is abandoned.
                var sealedRecord = completed.Seal();
                if (context.Attempt is { } attempt)
                {
                    attempt.OnKept(() => this.channel.TryWrite(sealedRecord));
                }
                else
                {
                    this.channel.TryWrite(sealedRecord);
                }
            }
        }
    }

    /// <summary>
    /// Evicts dispatch groups that have taken no observation for <paramref name="staleTimeout"/> before
    /// <paramref name="now"/>. Incomplete dispatches (no terminal observation) would otherwise leak forever in
    /// <c>activeGroups</c>. The age is measured from a group's last observation, not its first, so a long dispatch that
    /// keeps observing stays open. Each evicted group is sealed and flushed as an incomplete/failed record rather than
    /// silently dropped, preserving the partial audit trail. A terminal observation that arrives for it afterwards is
    /// dropped, the record already written standing, and logged at Debug (EventId 522).
    /// </summary>
    /// <param name="now">The current time used to evaluate inactivity. Injected for deterministic testing.</param>
    /// <param name="staleTimeout">The inactivity beyond which an incomplete group is considered stale.</param>
    public void EvictStaleGroups(DateTime now, TimeSpan staleTimeout)
    {
        foreach (var entry in this.activeGroups)
        {
            if (entry.Value.SealIfInactive(now, staleTimeout) is not { } evicted)
            {
                continue;
            }

            // Remembered before the group leaves the dictionary, so a terminal observation that no longer finds the
            // group there finds its context here.
            this.evictedContexts.AddOrUpdate(entry.Value.Context, EvictedMarker);
            this.activeGroups.TryRemove(entry);
            this.channel.TryWrite(evicted);
        }
    }

    private static bool IsTerminal(MessageObservation observation) => observation is DispatchCompleted or DispatchFailed;

    [LoggerMessage(
        EventId = 522,
        EventName = "TerminalObservationAfterEviction",
        Level = LogLevel.Debug,
        Message = "Dispatch {MessageId} ended ({Observation}) after its audit group was evicted as stale; the record " +
            "already written says it was evicted, and this observation is dropped")]
    private static partial void LogTerminalAfterEviction(ILogger logger, string messageId, string observation);
}
