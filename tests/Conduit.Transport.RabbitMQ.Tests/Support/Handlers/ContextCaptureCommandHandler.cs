using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class ContextCaptureCommandHandler(SpyLog spyLog)
    : ICommandHandler<ContextCaptureCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(
        ContextCaptureCommand command, CancellationToken cancellationToken)
    {
        var ctx = MessageContext.Current;
        var snapshot = new MessageContextSnapshot(ctx?.MessageId, ctx?.CausationId, ctx?.ConversationId);
        spyLog.Record(snapshot);
        return Task.FromResult(new TestCommandResult($"captured:{command.Input}"));
    }
}
