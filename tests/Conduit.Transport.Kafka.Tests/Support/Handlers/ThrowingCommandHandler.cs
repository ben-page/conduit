using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Transport.Kafka.Tests.Support.Contracts;

namespace Conduit.Transport.Kafka.Tests.Support.Handlers;

public class ThrowingCommandHandler : ICommandHandler<ThrowingCommand, TestCommandResult>
{
    public Task<TestCommandResult> HandleAsync(ThrowingCommand command, CancellationToken cancellationToken)
    {
        throw new NotFoundException($"Item '{command.Input}' not found");
    }
}
