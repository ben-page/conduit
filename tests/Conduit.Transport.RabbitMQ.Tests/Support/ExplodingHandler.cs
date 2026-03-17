using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class ExplodingHandler : ICommandHandler<ExplodingCommand>
{
    public Task HandleAsync(ExplodingCommand command, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Handler exploded");
    }
}
