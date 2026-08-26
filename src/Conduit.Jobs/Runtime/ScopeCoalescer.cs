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
    private readonly ILogger<ScopeCoalescer> logger;

    /// <summary>Initializes a new coalescer.</summary>
    /// <param name="store">The store to read and write scopes through.</param>
    /// <param name="logger">The logger for merge contention.</param>
    public ScopeCoalescer(IJobStore store, ILogger<ScopeCoalescer> logger)
    {
        this.store = store;
        this.logger = logger;
    }

    /// <summary>
    /// Merges the event's command into the trigger's scope, opening one if there is none.
    /// </summary>
    /// <param name="trigger">The coalescing trigger.</param>
    /// <param name="eventInstance">The triggering event.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The scope now holding the work, or <see langword="null"/> when contention was not resolved.</returns>
    public async Task<JobScope?> MergeAsync(
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

            var pending = JsonSerializer.Deserialize(
                existing.Payload, trigger.CommandType!, ConduitRuntime.JsonOptions)!;
            var incoming = trigger.CreateCommand(eventInstance);
            var merged = trigger.Merge!(pending, incoming);
            var payload = JsonSerializer.Serialize(merged, trigger.CommandType!, ConduitRuntime.JsonOptions);

            if (await this.store.TryMergeScopeAsync(
                scopeKey, payload, existing.Version, cancellationToken))
            {
                return new JobScope
                {
                    ScopeKey = existing.ScopeKey,
                    TriggerName = existing.TriggerName,
                    MessageType = existing.MessageType,
                    Payload = payload,
                    DueAt = existing.DueAt,
                    Version = existing.Version + 1,
                };
            }
        }

        LogMergeContended(this.logger, scopeKey, MaxMergeAttempts);
        return null;
    }

    private static JobScope BuildScope(
        JobTrigger trigger, object eventInstance, string scopeKey, DateTimeOffset now)
    {
        var command = trigger.CreateCommand(eventInstance);
        return new JobScope
        {
            ScopeKey = scopeKey,
            TriggerName = trigger.Name,
            MessageType = command.GetType().FullName!,
            Payload = JsonSerializer.Serialize(command, command.GetType(), ConduitRuntime.JsonOptions),

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
}
