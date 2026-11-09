using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class SetItemStatusHandler : ICommandHandler<SetItemStatusCommand, string>
{
    public Task<string> HandleAsync(SetItemStatusCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Status.ToString());
}
