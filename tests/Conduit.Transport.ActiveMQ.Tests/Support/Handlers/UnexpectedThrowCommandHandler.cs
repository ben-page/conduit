using Conduit.Handlers;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class UnexpectedThrowCommandHandler : ICommandHandler<UnexpectedThrowCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(UnexpectedThrowCommand command, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Something went terribly wrong");
    }
}
