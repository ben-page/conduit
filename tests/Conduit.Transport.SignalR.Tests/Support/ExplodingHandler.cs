using Conduit.Handlers;

namespace Conduit.Transport.SignalR.Tests.Support;

public class ExplodingHandler : ICommandHandler<ExplodingCommand>
{
    public Task HandleAsync(ExplodingCommand command, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Kaboom!");
    }
}
