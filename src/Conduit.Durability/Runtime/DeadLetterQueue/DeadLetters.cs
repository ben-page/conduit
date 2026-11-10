using System.Text.Json;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.DeadLetterQueue;

/// <summary>
/// Orchestrates dead letter operations: query, replay, and discard.
/// Replay re-publishes messages to the broker via the source transport's
/// <see cref="IConduitTransport.RedeliverAsync"/>, under the entry's original correlation ids.
/// </summary>
public sealed class DeadLetters : IDeadLetters
{
    private readonly IDeadLetterStore store;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<DeadLetters> logger;

    private static readonly Action<ILogger, string, Exception?> LogReplaySucceeded =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(301, "DlqReplaySucceeded"),
            "Replayed dead letter '{MessageType}'");

    private static readonly Action<ILogger, string, Exception?> LogReplayFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(302, "DlqReplayFailed"),
            "Failed to replay dead letter '{MessageType}'");

    /// <summary>
    /// Initializes a new instance of <see cref="DeadLetters"/>.
    /// </summary>
    public DeadLetters(
        IDeadLetterStore store,
        IServiceProvider serviceProvider,
        ILogger<DeadLetters> logger)
    {
        this.store = store;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filter.PageSize, DeadLetterFilter.MaxPageSize);

        return this.store.QueryAsync(filter, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
        => this.store.GetAsync(id, cancellationToken);

    /// <inheritdoc/>
    public async Task<ReplayResult> ReplayAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await this.store.GetAsync(id, cancellationToken);
        if (entry is null)
        {
            return new ReplayResult
            {
                DeadLetterId = id,
                Success = false,
                ErrorMessage = "Dead letter entry not found",
            };
        }

        try
        {
            var transport = this.serviceProvider.GetRequiredKeyedService<IConduitTransport>(entry.SourceEndpoint);
            var payload = JsonSerializer.Deserialize<JsonElement>(entry.Body, ConduitRuntime.JsonOptions);
            var envelope = new DispatchEnvelope(entry.MessageType, payload);

            // The replay carries the ids the message first arrived with, so its handling (and its
            // audit trail) continues the original's lineage rather than starting a new conversation.
            // A capture records all three or none (none when it ran outside a context): none mints fresh ones.
            var ids = string.IsNullOrWhiteSpace(entry.MessageId)
                || string.IsNullOrWhiteSpace(entry.CausationId)
                || string.IsNullOrWhiteSpace(entry.ConversationId)
                ? null
                : new RedeliveryIds(entry.MessageId, entry.CausationId, entry.ConversationId);

            await transport.RedeliverAsync(envelope, ids, cancellationToken);
            await this.store.RemoveAsync(id, cancellationToken);

            LogReplaySucceeded(this.logger, entry.MessageType, null);
            ConduitTelemetry.RecordReplayed(entry.MessageType);
            return new ReplayResult
            {
                DeadLetterId = id,
                Success = true,
                ErrorMessage = null,
            };
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReplayFailed(this.logger, entry.MessageType, ex);
            return new ReplayResult
            {
                DeadLetterId = id,
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReplayResult>> ReplayAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var results = new List<ReplayResult>();
        foreach (var id in ids)
        {
            results.Add(await this.ReplayAsync(id, cancellationToken));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReplayResult>> ReplayByTypeAsync(
        string messageType,
        CancellationToken cancellationToken)
    {
        // Every id first, a page at a time, then the replays: a replay removes its entry, which would move the entries
        // of the pages still to read. Distinct, since a capture made meanwhile moves an entry onto the next page. A
        // store that hands back no entries, or the token it was given, has no further page to read.
        var ids = new List<Guid>();
        string? pageToken = null;
        while (true)
        {
            var page = await this.store.QueryAsync(
                new DeadLetterFilter
                {
                    MessageType = messageType,
                    PageSize = DeadLetterFilter.MaxPageSize,
                    PageToken = pageToken,
                },
                cancellationToken);
            ids.AddRange(page.Entries.Select(entry => entry.Id));
            if (page.NextPageToken is null || page.Entries.Count == 0 || page.NextPageToken == pageToken)
            {
                break;
            }

            pageToken = page.NextPageToken;
        }

        return await this.ReplayAsync(ids.Distinct(), cancellationToken);
    }

    /// <inheritdoc/>
    public Task DiscardAsync(Guid id, CancellationToken cancellationToken)
        => this.store.RemoveAsync(id, cancellationToken);

    /// <inheritdoc/>
    public Task DiscardAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
        => this.store.RemoveAsync(ids, cancellationToken);
}
