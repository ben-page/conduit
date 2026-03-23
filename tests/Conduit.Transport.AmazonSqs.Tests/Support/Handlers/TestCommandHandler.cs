using Conduit.Handlers;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

public class TestCommandHandler(SpyLog spyLog) : ICommandHandler<TestCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(TestCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.FromResult(new TestCommandResult($"handled:{command.Input}"));
    }
}
