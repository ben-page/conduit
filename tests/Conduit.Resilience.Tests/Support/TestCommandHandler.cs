using Conduit.Handlers;

namespace Conduit.Resilience.Tests.Support;

public class TestCommandHandler : ICommandHandler<TestCommand, string>
{
    public Task<string> HandleAsync(TestCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Value);
}
