using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class ThrowUnauthorizedHandler : ICommandHandler<ThrowUnauthorizedCommand>
{
    public Task HandleAsync(ThrowUnauthorizedCommand command, CancellationToken cancellationToken)
        => throw new UnauthorizedException("Access denied");
}
