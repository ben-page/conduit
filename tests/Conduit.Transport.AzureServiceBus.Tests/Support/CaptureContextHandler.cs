using Conduit.Dispatch;
using Conduit.Handlers;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public class CaptureContextHandler : ICommandHandler<CaptureContextCommand, MessageContextSnapshot>
{
    public Task<MessageContextSnapshot> HandleAsync(CaptureContextCommand command, CancellationToken cancellationToken)
    {
        var context = MessageContext.Current ?? throw new InvalidOperationException("No MessageContext is active");
        var snapshot = new MessageContextSnapshot(context.MessageId, context.CausationId, context.ConversationId);
        return Task.FromResult(snapshot);
    }
}
