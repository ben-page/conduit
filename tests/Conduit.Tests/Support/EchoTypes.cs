using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

public record EchoCommand(string Value) : ICommand<string>;

public class EchoHandler : ICommandHandler<EchoCommand, string>
{
    public Task<string> HandleAsync(EchoCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Value);
}
