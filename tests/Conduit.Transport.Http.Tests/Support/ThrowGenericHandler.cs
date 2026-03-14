using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class ThrowGenericHandler : ICommandHandler<ThrowGenericCommand>
{
    public Task HandleAsync(ThrowGenericCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Something went wrong");
}
