using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Audit.Runtime;

internal sealed class DispatchGroup
{
    private readonly List<MessageObservation> observations = [];
    private readonly string messageId;
    private readonly string conversationId;
    private readonly string? causationId;
    private readonly IReadOnlyDictionary<string, object?>? properties;
    private readonly string serviceName;
    private JsonElement? payload;

    internal DispatchGroup(MessageContext context, string serviceName)
    {
        this.messageId = context.MessageId;
        this.conversationId = context.ConversationId;
        this.causationId = context.CausationId;
        this.properties = context.Properties.Count > 0
            ? new Dictionary<string, object?>(context.Properties)
            : null;
        this.payload = context.Payload;
        this.CreatedAt = DateTime.UtcNow;
        this.serviceName = serviceName;
    }

    internal DateTime CreatedAt { get; }

    internal void Add(MessageObservation observation) => this.observations.Add(observation);

    /// <summary>
    /// Omits the captured payload body from the sealed record. Used for types marked via
    /// <c>SuppressPayload&lt;T&gt;()</c> — observations are still recorded, but no body is persisted.
    /// </summary>
    internal void SuppressPayload() => this.payload = null;

    /// <summary>
    /// Appends a synthetic <see cref="DispatchFailed"/> observation marking this group as evicted
    /// before it emitted a terminal observation. Preserves the partial trail instead of dropping it.
    /// </summary>
    internal void MarkIncomplete(TimeSpan staleTimeout)
    {
        this.observations.Add(new DispatchFailed(
            DurationMs: 0,
            ErrorMessage: $"Dispatch evicted as stale: no terminal observation within {staleTimeout}.",
            ExceptionType: "Conduit.Audit.StaleGroupEviction"));
    }

    internal DispatchAuditRecord Seal()
    {
        var invoked = this.observations.OfType<DispatchInvoked>().FirstOrDefault();

        return new DispatchAuditRecord
        {
            MessageId = this.messageId,
            ConversationId = this.conversationId,
            CausationId = this.causationId,
            MessageType = invoked?.MessageType ?? "",
            MessageKind = invoked?.MessageKind ?? "",
            ServiceName = this.serviceName,
            Payload = this.payload,
            Properties = this.properties,
            Timestamp = this.CreatedAt,
            Observations = this.observations.ToList(),
        };
    }
}
