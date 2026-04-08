using Conduit.Middleware;
using Conduit.Outbox.Store;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Outbox.Runtime;

/// <summary>
/// Scoped <see cref="IOutboundInterceptor"/> implementation that buffers
/// outbound dispatch envelopes for the OutboxBehavior pipeline behavior.
/// Also implements <see cref="IOutboxFlusher"/> for immediate send after
/// the handler's transaction commits.
/// Registered as scoped — one instance per dispatch scope.
/// </summary>
public sealed partial class OutboxCapture : IOutboundInterceptor, IOutboxFlusher
{
    private readonly IOutboxStore store;
    private readonly TransportDispatcher transportDispatcher;
    private readonly ILogger<OutboxCapture> logger;
    private readonly List<DispatchEnvelope> captured = [];
    private List<WrittenOutboxMessage> writtenMessages = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxCapture"/> class.
    /// </summary>
    public OutboxCapture(
        IOutboxStore store,
        TransportDispatcher transportDispatcher,
        ILogger<OutboxCapture> logger)
    {
        this.store = store;
        this.transportDispatcher = transportDispatcher;
        this.logger = logger;
    }

    /// <summary>The envelopes captured during this scope.</summary>
    public IReadOnlyList<DispatchEnvelope> Captured => this.captured;

    /// <inheritdoc />
    public void Intercept(DispatchEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.captured.Add(envelope);
    }

    /// <summary>
    /// Called by <see cref="OutboxBehavior{TRequest,TResult}"/> after writing
    /// entries to the store. Correlates database entries with in-memory envelopes.
    /// </summary>
    internal void SetWrittenMessages(IReadOnlyList<WrittenOutboxMessage> messages)
    {
        this.writtenMessages = [.. messages];
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (this.writtenMessages.Count == 0)
        {
            return;
        }

        var messageIds = this.writtenMessages
            .Select(m => m.MessageId)
            .Distinct()
            .ToList();

        var claimed = await this.store.ClaimByMessageIdsAsync(messageIds, cancellationToken);
        if (claimed.Count == 0)
        {
            return;
        }

        // Build lookup: MessageId → queue of envelopes for correlation
        var envelopesByMessageId = new Dictionary<string, Queue<WrittenOutboxMessage>>();
        foreach (var written in this.writtenMessages)
        {
            if (!envelopesByMessageId.TryGetValue(written.MessageId, out var queue))
            {
                queue = new Queue<WrittenOutboxMessage>();
                envelopesByMessageId[written.MessageId] = queue;
            }

            queue.Enqueue(written);
        }

        var previousContext = MessageContext.Current;
        try
        {
            foreach (var entry in claimed)
            {
                if (!envelopesByMessageId.TryGetValue(entry.MessageId, out var queue) || queue.Count == 0)
                {
                    continue;
                }

                var written = queue.Dequeue();

                try
                {
                    // Restore original MessageContext for correlation
                    MessageContext.StartDispatchFromTransport(
                        written.MessageId,
                        written.CausationId,
                        written.ConversationId);

                    await this.transportDispatcher.SendAsync(written.Envelope, cancellationToken);

                    await this.store.MarkSentAsync(entry.Id, cancellationToken);
                }
#pragma warning disable CA1031
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    LogFlushSendFailed(this.logger, entry.Id, written.Envelope.Type, exception);
                    await this.store.IncrementRetryAsync(entry.Id, cancellationToken);
                }
            }
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to flush outbox entry {EntryId} (type: {Type}), leaving for relay")]
    private static partial void LogFlushSendFailed(ILogger logger, long entryId, string type, Exception exception);
}
