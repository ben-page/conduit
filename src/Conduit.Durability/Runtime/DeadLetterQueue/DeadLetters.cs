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
/// <remarks>
/// A replay claims its entry (<see cref="IDeadLetterStore.ClaimForReplayAsync"/>), redelivers it, then removes it; a
/// failed redelivery releases the claim. The claim is the guard against a second replay of the same entry, which is
/// refused while the first holds it. Delivery is at least once: a process that stops after the redelivery and before
/// the removal leaves the entry claimed, and the first replay after the claim times out re-publishes it once more.
/// </remarks>
internal sealed class DeadLetters : IDeadLetters
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

    private static readonly Action<ILogger, string, Guid, Exception?> LogReplayedEntryNotRemoved =
        LoggerMessage.Define<string, Guid>(
            LogLevel.Warning,
            new EventId(303, "DlqReplayedEntryNotRemoved"),
            "Replayed dead letter '{MessageType}', but could not remove entry {DeadLetterId}: it stays claimed, and a replay after its claim times out re-publishes it again");

    private static readonly Action<ILogger, string, Guid, Exception?> LogReplayClaimNotReleased =
        LoggerMessage.Define<string, Guid>(
            LogLevel.Warning,
            new EventId(304, "DlqReplayClaimNotReleased"),
            "Could not release the replay claim on dead letter '{MessageType}' entry {DeadLetterId}: it can be replayed again once the claim times out");

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
            return Failed(id, "Dead letter entry not found");
        }

        // The claim is the guard: one replay at a time redelivers an entry, so a second replay of it (another operator,
        // another host) is refused instead of publishing the message twice.
        var claimToken = await this.store.ClaimForReplayAsync(id, cancellationToken);
        if (claimToken is null)
        {
            return Failed(id, "Dead letter entry is already being replayed, or was removed");
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
        }
#pragma warning disable CA1031 // A failed redelivery is the replay's result, not an exception; the claim is released for the next replay.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReplayFailed(this.logger, entry.MessageType, ex);
            await this.ReleaseClaimAsync(id, entry.MessageType, claimToken);
            return Failed(id, ex.Message);
        }

        // Redelivered, so the replay succeeded whatever happens to the entry now; the removal settles it even if the
        // caller has since cancelled. A removal that fails (or a process that stops before it) leaves the entry claimed,
        // and the first replay after the claim times out re-publishes it once more: at least once, never lost.
        try
        {
            await this.store.RemoveAsync(id, CancellationToken.None);
        }
#pragma warning disable CA1031 // The message is already redelivered: a failed removal leaves the entry claimed until its claim times out.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReplayedEntryNotRemoved(this.logger, entry.MessageType, id, ex);
        }

        LogReplaySucceeded(this.logger, entry.MessageType, null);
        ConduitTelemetry.RecordReplayed(entry.MessageType);
        return new ReplayResult
        {
            DeadLetterId = id,
            Success = true,
            ErrorMessage = null,
        };
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

    private static ReplayResult Failed(Guid id, string errorMessage) => new()
    {
        DeadLetterId = id,
        Success = false,
        ErrorMessage = errorMessage,
    };

    // Frees the entry for the next replay after a failed redelivery, even if the caller has cancelled. A release that
    // fails is logged: the claim then times out instead.
    private async Task ReleaseClaimAsync(Guid id, string messageType, string claimToken)
    {
        try
        {
            await this.store.ReleaseReplayClaimAsync(id, claimToken, CancellationToken.None);
        }
#pragma warning disable CA1031 // A release that fails leaves the claim to time out; it must not replace the redelivery's own failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReplayClaimNotReleased(this.logger, messageType, id, ex);
        }
    }
}
