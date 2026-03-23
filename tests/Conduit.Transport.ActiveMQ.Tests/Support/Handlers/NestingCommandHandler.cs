using Conduit.Handlers;
using Conduit.Runtime;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class NestingCommandHandler(IMediator mediator, SpyLog spyLog)
    : ICommandHandler<NestingCommand, TestCommandResult>
{
    public async Task<TestCommandResult> HandleAsync(
        NestingCommand command, CancellationToken cancellationToken)
    {
        // Capture pre-nested context
        var ctx = MessageContext.Current;
        var outerSnapshot = new MessageContextSnapshot(ctx?.MessageId, ctx?.CausationId, ctx?.ConversationId);
        spyLog.Record(outerSnapshot);

        // Dispatch nested command locally
        var nestedResult = await mediator.ExecuteAsync(
            new TestCommand(command.Input), cancellationToken);

        return new TestCommandResult($"nested:{nestedResult.Value}");
    }
}
