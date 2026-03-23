using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

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
