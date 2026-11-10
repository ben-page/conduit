using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Background service that polls the outbox table for pending entries, delivers local listeners
/// and sends them to brokers via registered transports, then prunes completed entries.
/// </summary>
/// <remarks>
/// Resolves the scoped <see cref="IOutboxStore"/> per sweep rather than constructor-injecting it:
/// <see cref="OutboxRelay"/> is a singleton hosted service, and a singleton must not
/// constructor-inject a scoped service — Development-mode scope validation
/// (<c>ValidateScopes = true</c>, e.g. the consuming app's <c>WebApplicationFactory</c> tests)
/// rejects the host at startup otherwise. Scope-per-sweep is a hard prerequisite for adoption,
/// not a style preference. The cleanup's cadence reads <paramref name="timeProvider"/> (null is
/// <see cref="TimeProvider.System"/>); the wait between sweeps is <see cref="OutboxOptions.RelayInterval"/> of real time.
/// </remarks>
public sealed partial class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    TransportDispatcher transportDispatcher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelay> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Table creation is handled by DurabilityTableInitializer on startup,
        // which honors OutboxOptions.AutoCreateTable.
        var lastCleanup = this.clock.GetUtcNow();

        // The exhaustion counter's series exists from the relay's first export, not from the first failure.
        ConduitTelemetry.InitializeOutboxRetriesExhausted();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.ProcessPendingAsync(stoppingToken);

                if (HasCleanup(options.Value)
                    && this.clock.GetUtcNow() - lastCleanup >= options.Value.CleanupInterval)
                {
                    await this.CleanupCompletedAsync(stoppingToken);
                    lastCleanup = this.clock.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // One failed sweep must not end the relay; the next interval sweeps again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, ex);
            }

            try
            {
                // Delay lives in its own try: a stop request during the wait must exit quietly,
                // the same as a stop request during the sweep above, rather than surface as an
                // unhandled TaskCanceledException from ExecuteAsync (logged as noise by the host).
                await Task.Delay(options.Value.RelayInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Processes a single batch of pending outbox entries: resolves the store from a fresh
    /// sweep-scoped provider, then for each claimed entry delivers local listeners (fresh scope
    /// per entry) before the transport send — mirroring <see cref="OutboxCapture.ClaimAndDeliverAsync"/>'s
    /// delivery contract (the routine <see cref="OutboxCapture.FlushAsync"/> schedules on the
    /// background dispatcher), so redelivery re-runs local listeners identically. Each entry's
    /// outcome is written under its claim's token: a claim another relay took over (it outlasted
    /// <see cref="OutboxOptions.ClaimTimeout"/>) is logged as lost and nothing more is written for
    /// that entry. Exposed as internal for testing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store returned a claimed entry without its claim token.</exception>
    internal async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        // Disposed asynchronously, as every scope here is: Dispose() throws for a scoped service that implements only
        // IAsyncDisposable.
        await using var sweepScope = scopeFactory.CreateAsyncScope();
        var store = sweepScope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var pending = await store.ClaimPendingAsync(
            options.Value.BatchSize,
            options.Value.GracePeriod,
            cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var previousContext = MessageContext.Current;
        foreach (var entry in pending)
        {
            // Every outcome below is written under the claim's token, so a claimed entry must carry it.
            var claimToken = entry.ClaimToken ?? throw new InvalidOperationException(
                $"{store.GetType().Name}.ClaimPendingAsync returned outbox entry {entry.Id} without its ClaimToken.");

            // Same delivery as the post-commit flush (OutboxDelivery): the entry's own scope, the dispatch inside it,
            // the outcome, and only then the scope's disposal.
            var entryScope = scopeFactory.CreateAsyncScope();
            var sent = false;
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(entry.Payload, ConduitRuntime.JsonOptions);
                var envelope = new DispatchEnvelope(entry.Type, payload);

                // Restore original MessageContext for correlation, with the publishing dispatch's
                // persisted properties: the same bag the post-commit flush delivered under, and an empty
                // one for an entry that carries none (Properties NULL)
                MessageContext.StartDispatchFromTransport(
                    entry.MessageId,
                    entry.CausationId,
                    entry.ConversationId,
                    OutboxProperties.Read(entry.Properties, options.Value.PersistedProperties));

                await OutboxDelivery.DispatchAsync(envelope, entryScope.ServiceProvider, transportDispatcher, cancellationToken);

                // The point of no return: nothing after a true result reaches the catch below.
                sent = await store.MarkSentAsync(entry.Id, claimToken, cancellationToken);
                if (!sent)
                {
                    OutboxLog.ClaimLost(logger, entry.Id, entry.Type);
                }
            }
#pragma warning disable CA1031 // One entry's failed delivery is retried or marked failed; it must not stop the rest of the sweep.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSendFailed(logger, entry.Id, entry.Type, entry.RetryCount, ex);

                if (entry.RetryCount + 1 >= options.Value.MaxRetries)
                {
                    // Reported as exhausted only once the row is Failed: a lost claim's entry belongs to the relay that
                    // took it over, which may yet deliver it.
                    if (await store.MarkFailedAsync(entry.Id, claimToken, cancellationToken))
                    {
                        LogRetriesExhausted(logger, entry.Id, entry.Type, entry.MessageId, null);
                        ConduitTelemetry.RecordOutboxRetriesExhausted(entry.Type);
                        this.Observe(new OutboxRetryExhausted(entry.Id), entry.Id);
                    }
                    else
                    {
                        OutboxLog.ClaimLost(logger, entry.Id, entry.Type);
                    }
                }
                else
                {
                    // Backing off, not retrying on the next sweep, is what lets an entry outlast a broker restart.
                    var retryDelay = OutboxBackoff.Delay(options.Value, entry.RetryCount + 1);
                    if (!await store.IncrementRetryAsync(entry.Id, claimToken, retryDelay, cancellationToken))
                    {
                        OutboxLog.ClaimLost(logger, entry.Id, entry.Type);
                    }
                }
            }
            finally
            {
                // After the outcome and outside the catch: neither a disposal failure nor an observer's can retry the
                // entry, and neither replaces a delivery failure the catch has already logged.
                await OutboxDelivery.DisposeEntryScopeAsync(entryScope, logger, entry.Id, entry.Type);
                if (sent)
                {
                    this.Observe(new OutboxSent(entry.Id), entry.Id);
                }

                // The entry's context ends with the entry: the next one (which may fail to read before it restores its
                // own) and the relay loop never run under it.
                MessageContext.RestoreContext(previousContext);
            }
        }
    }

    // Records an observation of an entry whose outcome is written: an observer that throws is logged, never treated as
    // the entry's failure.
    private void Observe(MessageObservation observation, long entryId)
    {
        try
        {
            MessageContext.Observe(observation);
        }
#pragma warning disable CA1031 // The entry's outcome is already written; an observer's failure must not change it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            OutboxLog.ObservationFailed(logger, observation.GetType().Name, entryId, exception);
        }
    }

    private static bool HasCleanup(OutboxOptions o)
        => o.CompletedRetention > TimeSpan.Zero || (o.FailedRetention ?? o.CompletedRetention) > TimeSpan.Zero;

    /// <summary>
    /// Deletes Sent entries sent longer ago than <see cref="OutboxOptions.CompletedRetention"/> (by <c>SentAt</c>) and
    /// Failed entries whose last attempt failed longer ago than <see cref="OutboxOptions.FailedRetention"/> (by
    /// <c>FailedAt</c>; the retention defaults to <c>CompletedRetention</c>), resolving the store from its own fresh
    /// scope. Each retention is applied only when it is above <see cref="TimeSpan.Zero"/>, so
    /// <c>Zero</c> disables that half of the cleanup. Exposed as internal for testing.
    /// </summary>
    internal async Task CleanupCompletedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var o = options.Value;

        if (o.CompletedRetention > TimeSpan.Zero)
        {
            await store.DeleteCompletedAsync(o.CompletedRetention, cancellationToken);
        }

        var failedRetention = o.FailedRetention ?? o.CompletedRetention;
        if (failedRetention > TimeSpan.Zero)
        {
            await store.DeleteFailedAsync(failedRetention, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send outbox entry {EntryId} (type: {Type}, retry: {RetryCount})")]
    private static partial void LogSendFailed(ILogger logger, long entryId, string type, int retryCount, Exception exception);

    // EventId 320 is a contract: deploy alerting keys on it (DMS deploy/README.md). Never renumber it.
    [LoggerMessage(EventId = 320, EventName = "OutboxRetriesExhausted", Level = LogLevel.Error,
        Message = "Outbox entry {EntryId} (type: {Type}, message: {MessageId}) exhausted retries, marking as failed")]
    private static partial void LogRetriesExhausted(ILogger logger, long entryId, string type, string messageId, Exception? exception);
}
