using System.Text.Json;
using Conduit.Jobs.Persistence;
using Conduit.Messages;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Dispatches one claimed run's command through <see cref="IMediator"/> and classifies how it ended.
/// </summary>
/// <remarks>
/// Retry has two layers and they are not alternatives. <b>Inner:</b> the job command flows through
/// <c>IPipelineBehavior</c>, so a registered Conduit.Resilience policy retries transient failures
/// in-process against the same claimed row — that layer decides how <i>this attempt</i> behaves.
/// <b>Outer:</b> if the handler still throws once the in-process policy is exhausted, the ledger
/// increments the attempt count and reschedules — that layer decides whether there is <i>another
/// attempt</i>, and it is the only one that survives a process restart.
/// </remarks>
internal sealed partial class JobRunner
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ConduitTypeRegistry typeRegistry;
    private readonly ILogger<JobRunner> logger;

    public JobRunner(
        IServiceScopeFactory scopeFactory,
        ConduitTypeRegistry typeRegistry,
        ILogger<JobRunner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.typeRegistry = typeRegistry;
        this.logger = logger;
    }

    /// <summary>Dispatches the run's command and reports how the attempt ended.</summary>
    /// <param name="run">The claimed run.</param>
    /// <param name="cancellationRequested">
    /// Reports whether cancellation was asked for, rather than shutdown. Read after the fact, since
    /// the two arrive through the same token and only their meaning differs.
    /// </param>
    /// <param name="stoppingToken">The host's stopping token, used to tell shutdown from cancellation.</param>
    /// <param name="jobToken">
    /// The job's own token. It is linked to the host's stopping token and to the heartbeat, so
    /// shutdown, an operator cancellation and a <c>MaxRuntime</c> breach all reach the handler the
    /// same way.
    /// </param>
    /// <returns>How the attempt ended, and the failure when there was one.</returns>
    public async Task<JobAttempt> ExecuteAsync(
        JobRun run,
        Func<bool> cancellationRequested,
        CancellationToken stoppingToken,
        CancellationToken jobToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(cancellationRequested);

        ICommand command;
        try
        {
            command = this.Rehydrate(run);
        }
#pragma warning disable CA1031 // A malformed payload is a failure of this run, not of the scheduler.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRehydrationFailed(this.logger, run.JobKey, run.MessageType, exception);
            return new JobAttempt(JobOutcome.Failed, exception);
        }

        await using var scope = this.scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        try
        {
            await mediator.ExecuteAsync(command, jobToken);
            return new JobAttempt(JobOutcome.Completed, Error: null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown wins over an operator cancellation that arrived in the same breath: the claim
            // is released untouched so another host resumes, rather than recording a terminal state.
            return new JobAttempt(JobOutcome.ClaimLost, Error: null);
        }
        catch (OperationCanceledException) when (cancellationRequested())
        {
            return new JobAttempt(JobOutcome.Cancelled, Error: null);
        }
#pragma warning disable CA1031 // Any handler failure is a job failure; the ledger decides what happens next.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogJobFailed(this.logger, run.JobKey, exception);
            return new JobAttempt(JobOutcome.Failed, exception);
        }
    }

    private ICommand Rehydrate(JobRun run)
    {
        // The payload is deserialized rather than rebuilt from the declaration, because the host that
        // claims a run is not necessarily the one that materialized it: a crashed host's occurrence is
        // recovered elsewhere, and a delayed event trigger no longer has its event instance.
        var commandType = this.typeRegistry.CommandTypes
            .FirstOrDefault(type => string.Equals(type.FullName, run.MessageType, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Job run '{run.JobKey}' names command type '{run.MessageType}', which this application " +
                "does not know. The type was removed or renamed after the run was written; delete the " +
                "run or restore the type.");

        return JsonSerializer.Deserialize(run.Payload, commandType, ConduitRuntime.JsonOptions) as ICommand
            ?? throw new InvalidOperationException(
                $"Job run '{run.JobKey}' has a payload that does not deserialize to {run.MessageType}.");
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job run '{JobKey}' could not be rehydrated from stored type '{MessageType}'")]
    private static partial void LogRehydrationFailed(
        ILogger logger, string jobKey, string messageType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job run '{JobKey}' failed")]
    private static partial void LogJobFailed(ILogger logger, string jobKey, Exception exception);
}
