using System.Text.Json;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.DeadLetterQueue;

/// <summary>
/// Orchestrates dead letter operations: query, replay, and discard.
/// Replay re-publishes messages to the broker via the source transport's
/// <see cref="IConduitTransport.RedeliverAsync"/>.
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
        => this.store.QueryAsync(filter, cancellationToken);

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

            await transport.RedeliverAsync(envelope, cancellationToken);
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
        var page = await this.store.QueryAsync(
            new DeadLetterFilter { MessageType = messageType, PageSize = int.MaxValue },
            cancellationToken);

        return await this.ReplayAsync(page.Entries.Select(e => e.Id), cancellationToken);
    }

    /// <inheritdoc/>
    public Task DiscardAsync(Guid id, CancellationToken cancellationToken)
        => this.store.RemoveAsync(id, cancellationToken);

    /// <inheritdoc/>
    public Task DiscardAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
        => this.store.RemoveAsync(ids, cancellationToken);
}
