using Conduit.Handlers;

namespace Conduit.Tests.Support;

public class EchoHandler : ICommandHandler<EchoCommand, string>
{
    public Task<string> HandleAsync(EchoCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Value);
}
