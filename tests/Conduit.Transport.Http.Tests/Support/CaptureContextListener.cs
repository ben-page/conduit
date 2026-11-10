using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Transport.Http.Tests.Support;

public class CaptureContextListener(CapturedContexts sink) : IEventListener<CaptureContextEvent>
{
    public Task HandleAsync(CaptureContextEvent @event, CancellationToken cancellationToken)
    {
        var context = MessageContext.Current;
        sink.Add(
            new MessageContextSnapshot(context?.MessageId, context?.CausationId, context?.ConversationId),
            context?.Properties);
        return Task.CompletedTask;
    }
}
