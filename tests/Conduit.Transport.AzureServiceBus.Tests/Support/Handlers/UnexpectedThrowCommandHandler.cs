using Conduit.Handlers;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Handlers;

public class UnexpectedThrowCommandHandler : ICommandHandler<UnexpectedThrowCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(UnexpectedThrowCommand command, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Something went terribly wrong");
    }
}
