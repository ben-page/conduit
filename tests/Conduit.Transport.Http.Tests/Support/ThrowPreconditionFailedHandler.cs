using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class ThrowPreconditionFailedHandler : ICommandHandler<ThrowPreconditionFailedCommand>
{
    public Task HandleAsync(ThrowPreconditionFailedCommand command, CancellationToken cancellationToken)
        => throw new PreconditionFailedException("Stale version");
}
