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
    private readonly JsonElement? payload;
    private readonly string serviceName;

    internal DispatchGroup(MessageContext context, string serviceName)
    {
        this.messageId = context.MessageId;
        this.conversationId = context.ConversationId;
        this.causationId = context.CausationId;
        this.properties = context.Properties.Count > 0
            ? new Dictionary<string, object?>(context.Properties)
            : null;
        this.payload = context.Payload;
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.serviceName = serviceName;
    }

    internal DateTimeOffset CreatedAt { get; }

    internal void Add(MessageObservation observation) => this.observations.Add(observation);

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
