using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class ContextCaptureEventListener(SpyLog spyLog) : IEventListener<ContextCaptureEvent>
{
    public Task HandleAsync(ContextCaptureEvent @event, CancellationToken cancellationToken)
    {
        var ctx = MessageContext.Current;
        var snapshot = new MessageContextSnapshot(ctx?.MessageId, ctx?.CausationId, ctx?.ConversationId);
        spyLog.Record(snapshot);
        return Task.CompletedTask;
    }
}
