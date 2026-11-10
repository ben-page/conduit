using Conduit.Handlers;

namespace Conduit.Jobs.Tests.Support;

/// <summary>Records on the spy that it ran, so a test can show a refused job body never reached it.</summary>
public sealed class FireAndForgetJobHandler : ICommandHandler<FireAndForgetJob>
{
    private readonly JobSpy spy;

    public FireAndForgetJobHandler(JobSpy spy)
    {
        this.spy = spy;
    }

    public Task HandleAsync(FireAndForgetJob command, CancellationToken cancellationToken)
    {
        this.spy.RecordStart("fire-and-forget");
        return Task.CompletedTask;
    }
}
