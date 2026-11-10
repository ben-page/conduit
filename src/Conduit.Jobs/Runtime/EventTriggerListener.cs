using System.Text.Json;
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Jobs.Persistence;
using Conduit.Messages;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Turns a declared event trigger into a durable run. One closed instance is registered per declared
/// trigger's event type.
/// </summary>
/// <remarks>
/// <para>
/// The event reaches exactly one host in every deployment shape — broker replicas share one
/// competing-consumer queue, a local publish is seen only by the publishing host, and under an
/// engaged outbox the row claim has already elected one host — so writing the run here does not
/// need a claim race to settle first.
/// </para>
/// <para>
/// Marked <see cref="NotAutoRegisteredAttribute"/> because it is an open generic: the source
/// generator's scan would otherwise record a listener for the literal type parameter and emit a
/// dispatch case for a type that does not exist.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The event type this listener reacts to.</typeparam>
[NotAutoRegistered]
internal sealed partial class EventTriggerListener<TEvent> : IEventListener<TEvent>
    where TEvent : IEvent
{
    private readonly ScheduleRegistry registry;
    private readonly IJobStore store;
    private readonly ClaimedJobExecutor executor;
    private readonly ScopeCoalescer coalescer;
    private readonly TimeProvider timeProvider;
    private readonly JobSweepSignal sweepSignal;
    private readonly ILogger<EventTriggerListener<TEvent>> logger;

    public EventTriggerListener(
        ScheduleRegistry registry,
        IJobStore store,
        ClaimedJobExecutor executor,
        ScopeCoalescer coalescer,
        TimeProvider timeProvider,
        JobSweepSignal sweepSignal,
        ILogger<EventTriggerListener<TEvent>> logger)
    {
        this.registry = registry;
        this.store = store;
        this.executor = executor;
        this.coalescer = coalescer;
        this.timeProvider = timeProvider;
        this.sweepSignal = sweepSignal;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task HandleAsync(TEvent @event, CancellationToken cancellationToken)
    {
        // Keyed on the delivered message's id, not this dispatch's: a broker redelivering the same event,
        // or the outbox relay redelivering an entry whose first local delivery failed, produces the same
        // run key and the insert-or-ignore collapses it — idempotence for free, with no dedup store. Each
        // outbox delivery attempt has its own MessageId, so keying on that would run the job once per attempt.
        var messageId = MessageContext.Current?.DeliveredMessageId ?? Guid.NewGuid().ToString("N");
        var now = this.timeProvider.GetUtcNow();

        foreach (var trigger in this.registry.Triggers)
        {
            if (trigger.EventType != typeof(TEvent))
            {
                continue;
            }

            await this.WriteRunAsync(trigger, @event, messageId, now, cancellationToken);
        }
    }

    private async Task WriteRunAsync(
        JobTrigger trigger,
        object instance,
        string messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (trigger.CoalesceKey is not null)
        {
            // Coalescing work goes to the scope table, not straight to a run: the run materializes
            // when the work starts, so the ledger holds one row per real execution. A merge that
            // cannot be landed throws rather than returning, so this event is never acked with its
            // work nowhere.
            var scope = await this.coalescer.MergeAsync(trigger, instance, now, cancellationToken);

            // Picked up here rather than left to the sweep whenever the scope is already due, which
            // covers an undelayed trigger and a delayed one whose delay has already elapsed. That
            // preserves the non-coalescing path's latency: this host received the event, so this
            // host runs it.
            if (scope.DueAt <= now)
            {
                await this.coalescer.PickUpAsync(scope, now, runInline: true, cancellationToken);
            }

            return;
        }

        var command = trigger.CreateCommand(instance);
        var run = new JobRun
        {
            JobKey = $"{trigger.Name}@{messageId}",
            ScheduleName = trigger.Name,
            MessageType = command.GetType().FullName!,
            Payload = JsonSerializer.Serialize(command, command.GetType(), ConduitRuntime.JsonOptions),
            RunAt = trigger.Delay is { } delay ? now + delay : now,
            Status = JobRunStatus.Pending,
        };

        if (trigger.Delay is not null)
        {
            // Written pending, never claimed. A claim held longer than ClaimTimeout looks abandoned,
            // so another host would take it and a thirty-minute delay would silently collapse into
            // two. The sweep picks it up when it comes due, once woken to reckon its sleep with this run.
            await this.store.TryMaterializeAsync(run, cancellationToken);
            this.sweepSignal.Wake();
            return;
        }

        // Immediate: this host received the event, so it runs it — insert already claimed and skip
        // the claim round-trip entirely. Two writes, and the second is the delete on success.
        var claimed = await this.store.InsertClaimedAsync(
            run, Guid.NewGuid().ToString("N"), now, cancellationToken);

        if (claimed is null)
        {
            LogAlreadyTriggered(this.logger, run.JobKey);
            return;
        }

        this.executor.Start(claimed);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job run '{JobKey}' already exists, so this delivery of the event is a redelivery and is ignored")]
    private static partial void LogAlreadyTriggered(ILogger logger, string jobKey);
}
