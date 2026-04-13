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
    private readonly ConcurrentDictionary<string, DispatchGroup> activeGroups = new();
    private readonly ChannelWriter<DispatchAuditRecord> channel;
    private readonly string serviceName;

    /// <summary>
    /// Creates a new audit observer that writes sealed records to the specified channel.
    /// </summary>
    /// <param name="channel">The channel writer for sealed audit records.</param>
    /// <param name="serviceName">The service name to include in audit records.</param>
    public AuditObserver(ChannelWriter<DispatchAuditRecord> channel, string serviceName)
    {
        this.channel = channel;
        this.serviceName = serviceName;
    }

    /// <inheritdoc />
    public void OnObservation(MessageObservation observation, MessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var group = this.activeGroups.GetOrAdd(
            context.MessageId,
            _ => new DispatchGroup(context, this.serviceName));

        group.Add(observation);

        if (observation is DispatchCompleted or DispatchFailed)
        {
            if (this.activeGroups.TryRemove(context.MessageId, out var completed))
            {
                this.channel.TryWrite(completed.Seal());
            }
        }
    }
}
