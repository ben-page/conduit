using Conduit.Middleware;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Scoped <see cref="IOutboundInterceptor"/> implementation that buffers
/// outbound dispatch envelopes for the OutboxBehavior pipeline behavior.
/// Also implements <see cref="IOutboxFlusher"/> for immediate send after
/// the handler's transaction commits.
/// Registered as scoped — one instance per dispatch scope.
/// </summary>
/// <remarks>
/// Capture is transaction-gated: <see cref="Intercept"/> declines (and the caller falls back to
/// the pre-outbox path) when no ambient database transaction is present, because a captured
/// envelope can only be persisted — and thus delivered — through the handler's transaction.
/// </remarks>
public sealed partial class OutboxCapture : IOutboundInterceptor, IOutboxFlusher
{
    private readonly IOutboxStore store;
    private readonly TransportDispatcher transportDispatcher;
    private readonly IConduitConnectionFactory connectionFactory;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<OutboxCapture> logger;
    private readonly List<DispatchEnvelope> captured = [];
    private List<WrittenOutboxMessage> writtenMessages = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxCapture"/> class.
    /// </summary>
    public OutboxCapture(
        IOutboxStore store,
        TransportDispatcher transportDispatcher,
        IConduitConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxCapture> logger)
    {
        this.store = store;
        this.transportDispatcher = transportDispatcher;
        this.connectionFactory = connectionFactory;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    /// <summary>The envelopes captured during this scope.</summary>
    public IReadOnlyList<DispatchEnvelope> Captured => this.captured;

    /// <inheritdoc />
    public bool Intercept(DispatchEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // No ambient transaction -> decline: OutboxBehavior could never persist this envelope, so
        // capturing it would silently drop the message (and suppress local F&F delivery). Declining
        // preserves pre-outbox semantics for background/non-pipeline publishes.
        if (this.connectionFactory.GetCurrentTransaction() is null)
        {
            return false;
        }

        this.captured.Add(envelope);
        return true;
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

                    // Local listeners first, from a fresh scope (mirrors fire-and-forget's
                    // fresh-scope semantics); then transports; then mark sent. Any failure leaves
                    // the entry pending for the relay — redelivery re-runs local listeners, so
                    // listeners must stay idempotent.
                    using var scope = this.scopeFactory.CreateScope();
                    var localDispatcher = scope.ServiceProvider.GetService<ILocalEnvelopeDispatcher>();

                    if (localDispatcher is not null && localDispatcher.DeliversLocally(written.Envelope.Type))
                    {
                        await localDispatcher.PublishLocalEnvelopeAsync(written.Envelope, scope.ServiceProvider, cancellationToken);
                    }

                    if (localDispatcher is null || !localDispatcher.IsLocalOnly(written.Envelope.Type))
                    {
                        await this.transportDispatcher.SendAsync(written.Envelope, cancellationToken);
                    }

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
