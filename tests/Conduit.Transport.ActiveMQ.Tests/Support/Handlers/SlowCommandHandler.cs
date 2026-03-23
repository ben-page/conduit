using Conduit.Handlers;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class SlowCommandHandler(SpyLog spyLog) : ICommandHandler<SlowCommand, TestCommandResult>
{
    public async Task<TestCommandResult> HandleAsync(SlowCommand command, CancellationToken cancellationToken)
    {
        await Task.Delay(command.DelayMs, cancellationToken);
        spyLog.Record(command);
        return new TestCommandResult($"slow:{command.Input}");
    }
}
