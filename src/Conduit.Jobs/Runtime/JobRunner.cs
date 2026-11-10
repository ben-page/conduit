using System.Text.Json;
using Conduit.Attributes;
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
            return new JobAttempt(JobOutcome.HandedOver, Error: null);
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

    /// <summary>
    /// Refuses a <c>[FireAndForget]</c> command as a job body. Its generated dispatch hands the handler to the
    /// background dispatcher and returns at once, so <see cref="ExecuteAsync"/> would record the run Completed
    /// before the handler had started: <c>MaxRuntime</c>, cancellation, retries and history would all describe
    /// something other than the work. The attribute is read with inheritance, as the source generator reads it.
    /// </summary>
    /// <param name="commandType">The command type the job would dispatch.</param>
    /// <param name="owner">Who declares or holds the body, for the message: <c>Job schedule 'nightly'</c>.</param>
    /// <exception cref="InvalidOperationException">The command is <c>[FireAndForget]</c>.</exception>
    internal static void RequireInlineBody(Type commandType, string owner)
    {
        if (commandType.IsDefined(typeof(FireAndForgetAttribute), inherit: true))
        {
            throw new InvalidOperationException(
                $"{owner} runs {commandType.FullName}, a [FireAndForget] command. A job body must be a command " +
                "whose handler runs inline: a fire-and-forget handler runs in the background after the run is " +
                "recorded Completed, out of reach of MaxRuntime, cancellation, retries and run history. Remove " +
                "[FireAndForget] from the command, or run a command whose handler dispatches it.");
        }
    }

    /// <summary>
    /// <see cref="RequireInlineBody(Type, string)"/> for a command just built, whose runtime type a factory
    /// declared with a wider type can make <c>[FireAndForget]</c>.
    /// </summary>
    /// <param name="command">The command built for a run.</param>
    /// <param name="owner">Who declares the body, for the message.</param>
    /// <returns><paramref name="command"/>.</returns>
    /// <exception cref="InvalidOperationException">The command is <c>[FireAndForget]</c>.</exception>
    internal static ICommand RequireInlineBody(ICommand command, string owner)
    {
        RequireInlineBody(command.GetType(), owner);
        return command;
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

        // A run written before its type became [FireAndForget], or by a host that did not check: failing the
        // attempt is honest, where dispatching it would record Completed for work not yet begun.
        RequireInlineBody(commandType, $"Job run '{run.JobKey}'");

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
