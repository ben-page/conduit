using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class UnexpectedThrowCommandHandler : ICommandHandler<UnexpectedThrowCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(UnexpectedThrowCommand command, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Something went terribly wrong");
    }
}
