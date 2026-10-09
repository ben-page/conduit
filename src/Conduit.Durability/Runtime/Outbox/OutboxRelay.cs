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
/// not a style preference.
/// </remarks>
public sealed partial class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    TransportDispatcher transportDispatcher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Table creation is handled by DurabilityTableInitializer on startup,
        // which honors OutboxOptions.AutoCreateTable.
        var lastCleanup = DateTime.UtcNow;

        // The exhaustion counter's series exists from the relay's first export, not from the first failure.
        ConduitTelemetry.InitializeOutboxRetriesExhausted();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.ProcessPendingAsync(stoppingToken);

                if (HasCleanup(options.Value)
                    && DateTime.UtcNow - lastCleanup >= options.Value.CleanupInterval)
                {
                    await this.CleanupCompletedAsync(stoppingToken);
                    lastCleanup = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
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
    /// background dispatcher), so redelivery re-runs local listeners identically. Exposed as
    /// internal for testing.
    /// </summary>
    internal async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        using var sweepScope = scopeFactory.CreateScope();
        var store = sweepScope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var pending = await store.ClaimPendingAsync(
            options.Value.BatchSize,
            options.Value.GracePeriod,
            cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var entry in pending)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(entry.Payload, ConduitRuntime.JsonOptions);
                var envelope = new DispatchEnvelope(entry.Type, payload);

                // Restore original MessageContext for correlation
                MessageContext.StartDispatchFromTransport(
                    entry.MessageId,
                    entry.CausationId,
                    entry.ConversationId);

                // Same delivery contract as the post-commit flush: local listeners first (fresh
                // scope per entry), then transports, then mark sent. Redelivery re-runs local
                // listeners — the idempotent-listener requirement is load-bearing.
                using var entryScope = scopeFactory.CreateScope();
                var localDispatcher = entryScope.ServiceProvider.GetService<ILocalEnvelopeDispatcher>();

                if (localDispatcher is not null && localDispatcher.DeliversLocally(entry.Type))
                {
                    await localDispatcher.PublishLocalEnvelopeAsync(envelope, entryScope.ServiceProvider, cancellationToken);
                }

                if (localDispatcher is null || !localDispatcher.IsLocalOnly(entry.Type))
                {
                    await transportDispatcher.SendAsync(envelope, cancellationToken);
                }

                await store.MarkSentAsync(entry.Id, cancellationToken);
                MessageContext.Observe(new OutboxSent(entry.Id));
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSendFailed(logger, entry.Id, entry.Type, entry.RetryCount, ex);

                if (entry.RetryCount + 1 >= options.Value.MaxRetries)
                {
                    LogRetriesExhausted(logger, entry.Id, entry.Type, entry.MessageId, null);
                    ConduitTelemetry.RecordOutboxRetriesExhausted(entry.Type);
                    await store.MarkFailedAsync(entry.Id, cancellationToken);
                    MessageContext.Observe(new OutboxRetryExhausted(entry.Id));
                }
                else
                {
                    await store.IncrementRetryAsync(entry.Id, cancellationToken);
                }
            }
        }
    }

    private static bool HasCleanup(OutboxOptions o)
        => o.CompletedRetention > TimeSpan.Zero || (o.FailedRetention ?? o.CompletedRetention) > TimeSpan.Zero;

    /// <summary>
    /// Deletes Sent entries older than <see cref="OutboxOptions.CompletedRetention"/> and Failed entries older than
    /// <see cref="OutboxOptions.FailedRetention"/> (which defaults to <c>CompletedRetention</c>), resolving the store
    /// from its own fresh scope. Each retention is applied only when it is above <see cref="TimeSpan.Zero"/>, so
    /// <c>Zero</c> disables that half of the cleanup. Exposed as internal for testing.
    /// </summary>
    internal async Task CleanupCompletedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
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
