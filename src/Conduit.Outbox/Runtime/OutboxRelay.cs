using Conduit.Configuration;
using Conduit.Outbox.Configuration;
using Conduit.Outbox.Store;
using Conduit.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Conduit.Outbox.Runtime;

/// <summary>
/// Background service that polls the outbox table for pending entries
/// and sends them to brokers via registered transports.
/// </summary>
public sealed partial class OutboxRelay(
    IOutboxStore store,
    IEnumerable<IConduitTransport> transports,
    IServiceProvider serviceProvider,
    IOptions<OutboxOptions> options,
    ConduitOptions conduitOptions,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.AutoCreateTable)
        {
            await store.EnsureTableExistsAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.ProcessPendingAsync(stoppingToken);
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

            await Task.Delay(options.Value.RelayInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Processes a single batch of pending outbox entries. Exposed as internal for testing.
    /// </summary>
    internal async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await store.ClaimPendingAsync(
            options.Value.BatchSize,
            options.Value.GracePeriod,
            cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var transportArray = transports.ToArray();

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

                // Check if this type routes to a specific endpoint
                var endpoint = ConduitRuntime.ResolveRoute(conduitOptions, entry.Type);
                if (endpoint != null)
                {
                    var transport = ConduitRuntime.ResolveTransport(serviceProvider, endpoint);
                    await transport.SendRequestAsync(envelope, cancellationToken);
                }
                else
                {
                    // Event — broadcast to all transports
                    foreach (var transport in transportArray)
                    {
                        await transport.SendEventAsync(envelope, cancellationToken);
                    }
                }

                await store.MarkSentAsync(entry.Id, cancellationToken);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSendFailed(logger, entry.Id, entry.Type, entry.RetryCount, ex);

                if (entry.RetryCount + 1 >= options.Value.MaxRetries)
                {
                    LogRetriesExhausted(logger, entry.Id, null);
                    await store.MarkFailedAsync(entry.Id, cancellationToken);
                }
                else
                {
                    await store.IncrementRetryAsync(entry.Id, cancellationToken);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send outbox entry {EntryId} (type: {Type}, retry: {RetryCount})")]
    private static partial void LogSendFailed(ILogger logger, long entryId, string type, int retryCount, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox entry {EntryId} exhausted retries, marking as failed")]
    private static partial void LogRetriesExhausted(ILogger logger, long entryId, Exception? exception);
}
