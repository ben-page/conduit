using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class TestCommandHandler(SpyLog spyLog) : ICommandHandler<TestCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(TestCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.FromResult(new TestCommandResult($"handled:{command.Input}"));
    }
}
