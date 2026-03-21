using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class SlowCommandHandler(SpyLog spyLog) : ICommandHandler<SlowCommand, TestCommandResult>
{
    public async Task<TestCommandResult> HandleAsync(SlowCommand command, CancellationToken cancellationToken)
    {
        await Task.Delay(command.DelayMs, cancellationToken);
        spyLog.Record(command);
        return new TestCommandResult($"slow:{command.Input}");
    }
}
