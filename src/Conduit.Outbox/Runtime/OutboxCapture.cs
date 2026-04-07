using Conduit.Configuration;
using Conduit.Middleware;
using Conduit.Outbox.Store;
using Conduit.Runtime;

namespace Conduit.Outbox.Runtime;

/// <summary>
/// Scoped <see cref="IOutboundInterceptor"/> implementation that buffers
/// outbound dispatch envelopes for the OutboxBehavior pipeline behavior.
/// Also implements <see cref="IOutboxFlusher"/> for immediate send after
/// the handler's transaction commits.
/// Registered as scoped — one instance per dispatch scope.
/// </summary>
public sealed class OutboxCapture : IOutboundInterceptor, IOutboxFlusher
{
    private readonly IOutboxStore store;
    private readonly IConduitTransport[] transports;
    private readonly IServiceProvider serviceProvider;
    private readonly ConduitOptions conduitOptions;
    private readonly List<DispatchEnvelope> captured = [];
    private List<WrittenOutboxMessage> writtenMessages = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxCapture"/> class.
    /// </summary>
    public OutboxCapture(
        IOutboxStore store,
        IEnumerable<IConduitTransport> transports,
        IServiceProvider serviceProvider,
        ConduitOptions conduitOptions)
    {
        this.store = store;
        this.transports = transports.ToArray();
        this.serviceProvider = serviceProvider;
        this.conduitOptions = conduitOptions;
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

                // Check if this type routes to a specific endpoint
                var endpoint = ConduitRuntime.ResolveRoute(this.conduitOptions, written.Envelope.Type);
                if (endpoint != null)
                {
                    var transport = ConduitRuntime.ResolveTransport(this.serviceProvider, endpoint);
                    await transport.SendRequestAsync(written.Envelope, cancellationToken);
                }
                else
                {
                    // Event — broadcast to all transports
                    foreach (var transport in this.transports)
                    {
                        await transport.SendEventAsync(written.Envelope, cancellationToken);
                    }
                }

                await this.store.MarkSentAsync(entry.Id, cancellationToken);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                await this.store.IncrementRetryAsync(entry.Id, cancellationToken);
            }
        }
    }
}
