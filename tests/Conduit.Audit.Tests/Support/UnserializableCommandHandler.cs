using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

public sealed class UnserializableCommandHandler : ICommandHandler<UnserializableCommand>
{
    public Task HandleAsync(UnserializableCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
