using Conduit.Runtime;

namespace Conduit.TestUtils;

public sealed class ObservationCollector : IConduitObserver
{
    private readonly List<(MessageObservation Observation, string MessageId, string ConversationId)> collected = [];

    public IReadOnlyList<(MessageObservation Observation, string MessageId, string ConversationId)> Collected
        => this.collected;

    public void OnObservation(MessageObservation observation, MessageContext context)
    {
        this.collected.Add((observation, context.MessageId, context.ConversationId));
    }

    public IReadOnlyList<T> OfType<T>() where T : MessageObservation
        => this.collected.Select(c => c.Observation).OfType<T>().ToList();

    public void Clear() => this.collected.Clear();
}
