using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>
/// Records, per dispatch, the context's payload at the moment the dispatch closes (DispatchCompleted /
/// DispatchFailed) -- the moment Conduit.Audit's DispatchGroup now seals it.
/// </summary>
public sealed class ClosingPayloadObserver : IConduitObserver
{
    private readonly Dictionary<string, string> typeByMessageId = [];
    private readonly List<ClosedDispatch> closed = [];

    public IReadOnlyList<ClosedDispatch> Closed
    {
        get
        {
            lock (this.closed)
            {
                return this.closed.ToList();
            }
        }
    }

    public void OnObservation(MessageObservation observation, MessageContext context)
    {
        lock (this.closed)
        {
            if (observation is DispatchInvoked invoked)
            {
                this.typeByMessageId[context.MessageId] = invoked.MessageType;
                return;
            }

            if (observation is DispatchCompleted or DispatchFailed
                && this.typeByMessageId.TryGetValue(context.MessageId, out var messageType))
            {
                this.closed.Add(new ClosedDispatch(messageType, context.Payload?.GetRawText(), observation is DispatchFailed));
            }
        }
    }

    public ClosedDispatch ClosedOfType(string messageType) => this.Closed.Single(c => c.MessageType == messageType);
}

public sealed record ClosedDispatch(string MessageType, string? PayloadJson, bool Failed);
