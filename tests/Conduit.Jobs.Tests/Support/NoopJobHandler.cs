using Conduit.Handlers;

namespace Conduit.Jobs.Tests.Support;

public sealed class NoopJobHandler : ICommandHandler<NoopJob>
{
    public Task HandleAsync(NoopJob command, CancellationToken cancellationToken) => Task.CompletedTask;
}
