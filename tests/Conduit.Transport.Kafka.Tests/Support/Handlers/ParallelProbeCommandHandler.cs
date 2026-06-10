using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.Kafka.Tests.Support;
using Conduit.Transport.Kafka.Tests.Support.Contracts;

namespace Conduit.Transport.Kafka.Tests.Support.Handlers;

public class ParallelProbeCommandHandler(ConcurrencyProbe probe, SpyLog spyLog)
    : ICommandHandler<ParallelProbeCommand, TestCommandResult>
{
    public async Task<TestCommandResult> HandleAsync(
        ParallelProbeCommand command, CancellationToken cancellationToken)
    {
        probe.Enter();
        try
        {
            await Task.Delay(command.HoldMs, cancellationToken);
            spyLog.Record(command);
            return new TestCommandResult($"probe:{command.Input}");
        }
        finally
        {
            probe.Exit();
        }
    }
}
