using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

public class ShutdownProbeCommandHandler(SpyLog spyLog) : ICommandHandler<ShutdownProbeCommand, TestCommandResult>
{
    public async Task<TestCommandResult> HandleAsync(ShutdownProbeCommand command, CancellationToken cancellationToken)
    {
        // Recorded on entry, so the test knows which instance is running it
        spyLog.Record(command);

        // Deliberately not cancellable: the handler outlives a stop request, as real work does
        await Task.Delay(command.DelayMs, CancellationToken.None);
        return new TestCommandResult($"completed:{command.Input}");
    }
}
