using System.Text.Json;
using Conduit.Jobs.Persistence;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// The coalescing protocol: merge an event's command into the trigger's pending scope, and turn a due
/// scope into a run. The only place that knows how scopes and runs relate.
/// </summary>
/// <remarks>
/// Merging is read-modify-write in the host rather than an upsert, because the merge function is
/// caller-supplied C# and cannot be expressed in SQL without restricting merges to a fixed vocabulary.
/// The <see cref="JobScope.Version"/> compare-and-set is what makes that safe: a lost merge would
/// narrow the scope, which loses work rather than merely duplicating it.
/// </remarks>
internal sealed partial class ScopeCoalescer
{
    // Contention is between hosts racing on one coalesce key, so a handful of rounds is generous.
    // Bounded rather than unbounded so a pathological loop surfaces as an error, not a hang.
    private const int MaxMergeAttempts = 8;

    private readonly IJobStore store;
    private readonly ClaimedJobExecutor executor;
    private readonly ILogger<ScopeCoalescer> logger;

    /// <summary>Initializes a new coalescer.</summary>
    /// <param name="store">The store to read and write scopes through.</param>
    /// <param name="executor">Runs a scope this host is picking up inline.</param>
    /// <param name="logger">The logger for merge contention.</param>
    public ScopeCoalescer(IJobStore store, ClaimedJobExecutor executor, ILogger<ScopeCoalescer> logger)
    {
        this.store = store;
        this.executor = executor;
        this.logger = logger;
    }

    /// <summary>
    /// Merges the event's command into the trigger's scope, opening one if there is none. Either the
    /// work is in a scope when this returns, or it throws.
    /// </summary>
    /// <param name="trigger">The coalescing trigger.</param>
    /// <param name="eventInstance">The triggering event.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The scope now holding the work.</returns>
    /// <exception cref="InvalidOperationException">
    /// Contention was not resolved within <c>MaxMergeAttempts</c>. Returning quietly would ack the
    /// event with its work nowhere, which drops work; throwing lets the listener failure surface as
    /// an <c>EventListenerException</c> so the transport can retry or dead-letter it.
    /// </exception>
    public async Task<JobScope> MergeAsync(
        JobTrigger trigger,
        object eventInstance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var scopeKey = JobScope.ComposeKey(trigger.Name, trigger.CoalesceKey!(eventInstance));

        for (var attempt = 0; attempt < MaxMergeAttempts; attempt++)
        {
            var existing = await this.store.GetScopeAsync(scopeKey, cancellationToken);

            if (existing is null)
            {
                var opened = BuildScope(trigger, eventInstance, scopeKey, now);
                if (await this.store.TryInsertScopeAsync(opened, cancellationToken))
                {
                    return opened;
                }

                // Another host opened it in the same instant; re-read and merge into theirs.
                continue;
            }

            // Not reachable through the write path — BuildScope and the merge below both serialize a
            // non-null command — but a payload of "null" deserializes to null, and a bare
            // NullReferenceException out of the merge lambda would say nothing about which scope it
            // came from.
            var pending = JsonSerializer.Deserialize(
                existing.Payload, trigger.CommandType!, ConduitRuntime.JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Coalescing scope '{scopeKey}' holds a payload that deserializes to null as " +
                    $"'{trigger.CommandType!.FullName}', so there is nothing to merge into.");

            var incoming = trigger.CreateCommand(eventInstance);
            var merged = trigger.Merge!(pending, incoming);
            var payload = JsonSerializer.Serialize(merged, trigger.CommandType!, ConduitRuntime.JsonOptions);

            if (await this.store.TryMergeScopeAsync(
                scopeKey, existing.ScopeId, payload, existing.Version, cancellationToken))
            {
                return new JobScope
                {
                    ScopeKey = existing.ScopeKey,
                    ScopeId = existing.ScopeId,
                    TriggerName = existing.TriggerName,
                    MessageType = existing.MessageType,
                    Payload = payload,
                    DueAt = existing.DueAt,
                    Version = existing.Version + 1,
                };
            }
        }

        LogMergeContended(this.logger, scopeKey, MaxMergeAttempts);

        // Never returned quietly: the caller is an event listener, and returning would let the
        // dispatch complete — and the broker ack — with this event's work in no scope at all. Losing
        // work is the one outcome this module resolves every window against, so the failure
        // propagates as an EventListenerException and the transport retries or dead-letters it.
        throw new InvalidOperationException(
            $"Coalescing scope '{scopeKey}' was still contended after {MaxMergeAttempts} attempts, " +
            "so this event's work was not merged.");
    }

    /// <summary>Turns a due scope into a run and frees the coalescing slot.</summary>
    /// <param name="scope">The due scope.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="runInline">
    /// Whether this host runs it immediately. True on the host that received the event, mirroring the
    /// non-coalescing immediate path; false for the sweep, which materializes pending and lets the
    /// claim elect a runner.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// The run key is the scope key, so the run ledger holds at most one row per coalesce key and the
    /// existing insert-or-ignore is what enforces it. A refused insert therefore means "that key is
    /// already running", and the scope stays put as the accumulator for everything arriving since —
    /// which is what bounds a burst to one run in flight and one waiting behind it. The delete happens
    /// only on the branch that wrote the run, and is conditional on the version this pickup read, so a
    /// merge landing in between leaves the scope alive at a higher version and becomes its own run.
    /// <para>
    /// A run that stopped attempting — <see cref="JobRunStatus.Failed"/> or
    /// <see cref="JobRunStatus.Cancelled"/> — still holds the key, because only
    /// <see cref="IJobStore.CompleteAsync"/> deletes on success; a terminal row otherwise sits until
    /// the retention purge, up to <c>FailedRetention</c> later. Since the key <i>is</i> the coalesce
    /// key, leaving it held would silently stop every future scope for it from ever running. The
    /// attempts that were exhausted belonged to the occurrence that failed; the scope waiting behind
    /// it is different work, so pickup clears a terminal row for this key before materializing.
    /// </para>
    /// </remarks>
    public async Task PickUpAsync(
        JobScope scope, DateTimeOffset now, bool runInline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (await this.store.TryClearTerminalRunAsync(scope.ScopeKey, cancellationToken))
        {
            LogTerminalRunSuperseded(this.logger, scope.ScopeKey);
        }

        var run = new JobRun
        {
            JobKey = scope.ScopeKey,
            ScheduleName = scope.TriggerName,
            MessageType = scope.MessageType,
            Payload = scope.Payload,
            RunAt = scope.DueAt,
            Status = JobRunStatus.Pending,
        };

        if (!runInline)
        {
            if (await this.store.TryMaterializeAsync(run, cancellationToken))
            {
                await this.store.TryDeleteScopeAsync(scope.ScopeKey, scope.ScopeId, scope.Version, cancellationToken);
            }

            return;
        }

        var claimed = await this.store.InsertClaimedAsync(
            run, Guid.NewGuid().ToString("N"), now, cancellationToken);

        if (claimed is null)
        {
            // A run for this coalesce key is still outstanding, so the scope stays as the accumulator.
            return;
        }

        await this.store.TryDeleteScopeAsync(scope.ScopeKey, scope.ScopeId, scope.Version, cancellationToken);
        this.executor.Start(claimed);
    }

    private static JobScope BuildScope(
        JobTrigger trigger, object eventInstance, string scopeKey, DateTimeOffset now)
    {
        var command = trigger.CreateCommand(eventInstance);

        // The declared command type when the trigger has one, not the runtime type of this one
        // instance: the merge path always (de)serializes as trigger.CommandType, so the insert must
        // agree with it by construction, or a caller-widened type argument would leave MessageType
        // naming the derived type while the first merge silently rewrites the payload as the base type.
        var messageType = trigger.CommandType ?? command.GetType();

        return new JobScope
        {
            ScopeKey = scopeKey,

            // A fresh generation identity every time a scope is (re)opened. Version alone cannot
            // guard a compare-and-set that straddles a pickup, because Version resets to 0 on every
            // new generation — ScopeId is what lets a stale merge be told apart from a live one.
            ScopeId = Guid.NewGuid().ToString("N"),
            TriggerName = trigger.Name,
            MessageType = messageType.FullName!,
            Payload = JsonSerializer.Serialize(command, messageType, ConduitRuntime.JsonOptions),

            // Written once and never moved by a merge: leading edge, so a steady stream of events
            // cannot keep pushing the run away.
            DueAt = trigger.Delay is { } delay ? now + delay : now,
            Version = 0,
        };
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Coalescing scope '{ScopeKey}' was still contended after {Attempts} attempts; this event's scope was not merged")]
    private static partial void LogMergeContended(ILogger logger, string scopeKey, int attempts);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Coalesce key '{ScopeKey}' was held by a run that had stopped attempting; newer coalesced work has superseded it")]
    private static partial void LogTerminalRunSuperseded(ILogger logger, string scopeKey);
}
