using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Bridge.Http.Tests.Support;

public class ThrowConflictHandler : ICommandHandler<ThrowConflictCommand>
{
    public Task HandleAsync(ThrowConflictCommand command, CancellationToken cancellationToken)
        => throw new ConflictException("Already exists");
}
