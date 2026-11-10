using Conduit.Handlers;

namespace Conduit.Jobs.Tests.Support;

public sealed class FireAndForgetJobHandler : ICommandHandler<FireAndForgetJob>
{
    public Task HandleAsync(FireAndForgetJob command, CancellationToken cancellationToken) => Task.CompletedTask;
}
