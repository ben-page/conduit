using Conduit.Runtime;
using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class CaptureContextHandler : ICommandHandler<CaptureContextCommand, MessageContextSnapshot>
{
    public Task<MessageContextSnapshot> HandleAsync(CaptureContextCommand command, CancellationToken cancellationToken)
    {
        var context = MessageContext.Current ?? throw new InvalidOperationException("No MessageContext available");
        var snapshot = new MessageContextSnapshot(context.MessageId, context.CausationId, context.ConversationId);
        return Task.FromResult(snapshot);
    }
}
