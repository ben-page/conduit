using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class ConcurrencyProbeCommandHandler(SpyLog spyLog) : ICommandHandler<ConcurrencyProbeCommand, TestCommandResult>
{
    public async Task<TestCommandResult> HandleAsync(ConcurrencyProbeCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(new ConcurrencyProbeMark(command.Input, Entered: true));
        await Task.Delay(command.DelayMs, CancellationToken.None);
        spyLog.Record(new ConcurrencyProbeMark(command.Input, Entered: false));

        return new TestCommandResult($"probed:{command.Input}");
    }
}
