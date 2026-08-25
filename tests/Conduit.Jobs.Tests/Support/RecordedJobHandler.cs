using Conduit.Handlers;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// Records the run and honours the spy's duration and failure script, so long-running, failing and
/// instantaneous jobs need no separate handler types. The delay runs on the injected
/// <see cref="TimeProvider"/>, which is what makes "a job that runs for ten minutes" deterministic.
/// </summary>
public sealed class RecordedJobHandler : ICommandHandler<RecordedJob>
{
    private readonly JobSpy spy;
    private readonly TimeProvider timeProvider;

    public RecordedJobHandler(JobSpy spy, TimeProvider timeProvider)
    {
        this.spy = spy;
        this.timeProvider = timeProvider;
    }

    public async Task HandleAsync(RecordedJob command, CancellationToken cancellationToken)
    {
        this.spy.RecordStart(command.Label);

        if (this.spy.JobDuration > TimeSpan.Zero)
        {
            await Task.Delay(
                this.spy.JobDuration,
                this.timeProvider,
                this.spy.IgnoresCancellation ? CancellationToken.None : cancellationToken);
        }

        if (this.spy.ShouldFail())
        {
            throw new InvalidOperationException($"Job '{command.Label}' failed on purpose.");
        }

        this.spy.RecordCompletion(command.Label);
    }
}
