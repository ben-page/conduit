using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Transport.SignalR.Tests.Support;

public class CaptureContextHandler(CapturedContexts sink)
    : ICommandHandler<CaptureContextCommand, Guid>
{
    public Task<Guid> HandleAsync(CaptureContextCommand command, CancellationToken cancellationToken)
    {
        var context = MessageContext.Current;
        sink.Add(new MessageContextSnapshot(context?.MessageId, context?.CausationId, context?.ConversationId));
        return Task.FromResult(Guid.NewGuid());
    }
}
