using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class PingHandler : ICommandHandler<PingCommand, string>
{
    public Task<string> HandleAsync(PingCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult($"pong: {command.Message}");
    }
}
