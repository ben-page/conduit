using System.Diagnostics;
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
/// Also implements <see cref="IOutboxFlusher"/>: <see cref="FlushAsync"/> schedules immediate
/// post-commit delivery on the background dispatcher and returns — the caller never waits; the
/// relay remains the redelivery backstop.
/// Registered as scoped — one instance per dispatch scope.
/// </summary>
/// <remarks>
/// Capture is transaction-gated: <see cref="Intercept"/> declines (and the caller falls back to
/// the pre-outbox path) when no ambient database transaction is present, because a captured
/// envelope can only be persisted — and thus delivered — through the handler's transaction.
/// </remarks>
public sealed partial class OutboxCapture : IOutboundInterceptor, IOutboxFlusher
{
    private readonly TransportDispatcher transportDispatcher;
    private readonly IConduitConnectionFactory connectionFactory;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ConduitBackgroundDispatcher backgroundDispatcher;
    private readonly ILogger<OutboxCapture> logger;
    private readonly List<DispatchEnvelope> captured = [];
    private List<WrittenOutboxMessage> writtenMessages = [];

    // How many of `captured` (always a prefix — envelopes are written in capture order) already
    // have an outbox row in this scope's transaction.
    private int writtenCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxCapture"/> class.
    /// </summary>
    /// <remarks>
    /// Does not take an <see cref="IOutboxStore"/>: unlike the pre-background-flush shape, no
    /// method on this class touches the store directly anymore. <see cref="ClaimAndDeliverAsync"/>
    /// (the only store-touching code path left) resolves its own from a fresh scope, since it runs
    /// long after this instance's request scope — and any store it could have held — is gone.
    /// </remarks>
    public OutboxCapture(
        TransportDispatcher transportDispatcher,
        IConduitConnectionFactory connectionFactory,
        IServiceScopeFactory scopeFactory,
        ConduitBackgroundDispatcher backgroundDispatcher,
        ILogger<OutboxCapture> logger)
    {
        this.transportDispatcher = transportDispatcher;
        this.connectionFactory = connectionFactory;
        this.scopeFactory = scopeFactory;
        this.backgroundDispatcher = backgroundDispatcher;
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
    /// The captured envelopes that have no outbox row yet, in capture order — a snapshot.
    /// <see cref="OutboxBehavior{TRequest,TResult}"/> runs at the end of EVERY pipeline in the
    /// scope (including the nested pipelines of events a handler publishes inline), so it writes
    /// only these: writing all of <see cref="Captured"/> each time would give one envelope a row
    /// per pipeline.
    /// </summary>
    internal IReadOnlyList<DispatchEnvelope> Unwritten
        => this.captured.GetRange(this.writtenCount, this.captured.Count - this.writtenCount);

    /// <summary>
    /// Called by <see cref="OutboxBehavior{TRequest,TResult}"/> after its write to the store
    /// succeeded: marks the written envelopes (the leading <paramref name="messages"/>.Count of
    /// <see cref="Unwritten"/>) as written, and APPENDS their MessageId-to-envelope correlation to
    /// <see cref="WrittenMessages"/>, so the post-commit flush delivers every row this scope wrote
    /// — not only the last pipeline's.
    /// </summary>
    internal void AddWrittenMessages(IReadOnlyList<WrittenOutboxMessage> messages)
    {
        Debug.Assert(
            this.writtenCount + messages.Count <= this.captured.Count
            && messages.Select((message, index) => ReferenceEquals(message.Envelope, this.captured[this.writtenCount + index])).All(matches => matches),
            "Written messages must be the leading unwritten envelopes, in capture order.");

        this.writtenMessages.AddRange(messages);
        this.writtenCount += messages.Count;
    }

    /// <summary>
    /// Drops the envelopes captured at or after index <paramref name="capturedCount"/> that are
    /// still unwritten — called by <see cref="OutboxBehavior{TRequest,TResult}"/> when the pipeline
    /// that captured them faults, so a later pipeline in the same DI scope cannot write a failed
    /// unit of work's messages into its own transaction. Written envelopes already have a row in the
    /// transaction (its commit or rollback decides them) and are never dropped; nor are envelopes
    /// captured before <paramref name="capturedCount"/>, which belong to an enclosing handler.
    /// </summary>
    internal void DiscardUnwrittenCapturedSince(int capturedCount)
    {
        var start = Math.Max(capturedCount, this.writtenCount);
        if (start < this.captured.Count)
        {
            this.captured.RemoveRange(start, this.captured.Count - start);
        }
    }

    /// <summary>
    /// The messages added by <see cref="AddWrittenMessages"/> since the last flush. Exposed for tests that exercise
    /// <see cref="ClaimAndDeliverAsync"/> directly against the messages a real capture/behavior
    /// pipeline correlated, without going through the background dispatcher.
    /// </summary>
    internal IReadOnlyList<WrittenOutboxMessage> WrittenMessages => this.writtenMessages;

    /// <inheritdoc />
    /// <remarks>
    /// Schedules immediate post-commit delivery on <see cref="ConduitBackgroundDispatcher"/> and
    /// returns — the caller never waits; the relay remains the redelivery backstop. This restores
    /// <see cref="Conduit.Attributes.FireAndForgetAttribute"/>'s "caller returns immediately"
    /// contract under transactional capture: captured fire-and-forget is transactional AND
    /// asynchronous, not transactional-but-synchronous.
    /// <para>
    /// The scheduled work item runs under the dispatcher's own stopping token, not
    /// <paramref name="cancellationToken"/> — delivery is intentionally decoupled from the
    /// caller's request lifetime, so it must survive the request scope ending (and the request's
    /// cancellation firing). Accordingly, the closure captures only singletons
    /// (<see cref="IServiceScopeFactory"/>, <see cref="TransportDispatcher"/>, <see cref="ILogger"/>)
    /// and the plain-record <see cref="WrittenOutboxMessage"/> values — no scoped state, and in
    /// particular never this request scope's own <see cref="IOutboxStore"/> or
    /// <see cref="IConduitConnectionFactory"/>, both of which are gone by the time the work item
    /// runs.
    /// </para>
    /// </remarks>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (this.writtenMessages.Count == 0)
        {
            return Task.CompletedTask;
        }

        var messages = this.writtenMessages;
        this.writtenMessages = [];

        // Hoisted to locals before building the closure: a lambda that reads an instance field via
        // `this.field` captures `this` as a whole (a compiler-generated back-reference to the
        // entire OutboxCapture instance), not a copy of the field. Capturing `this` here would pin
        // this instance's other fields — notably the scoped connectionFactory and the captured
        // envelope list — reachable in memory for as long as the work item sits in the dispatcher's
        // channel, and would violate the "closure captures only these singletons" contract documented
        // on FlushAsync. Referencing only locals below means the closure captures exactly those
        // locals, nothing else.
        var scopeFactory = this.scopeFactory;
        var transportDispatcher = this.transportDispatcher;
        var logger = this.logger;

        this.backgroundDispatcher.Enqueue(
            ct => ClaimAndDeliverAsync(scopeFactory, transportDispatcher, logger, messages, ct));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Claims the outbox entries backing <paramref name="messages"/> and delivers each one:
    /// local listeners first (fresh DI scope per entry), then the transport send, then marks the
    /// entry sent; a failed entry is left for the relay to redeliver. This is the routine
    /// <see cref="FlushAsync"/> schedules on the background dispatcher — it resolves
    /// <see cref="IOutboxStore"/> from its own fresh scope rather than a captured scoped instance,
    /// because the scope that produced <paramref name="messages"/> is gone by the time this runs
    /// (mirrors <see cref="OutboxRelay.ProcessPendingAsync"/>'s scope-per-sweep pattern).
    /// </summary>
    internal static async Task ClaimAndDeliverAsync(
        IServiceScopeFactory scopeFactory,
        TransportDispatcher transportDispatcher,
        ILogger logger,
        IReadOnlyList<WrittenOutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        var messageIds = messages
            .Select(m => m.MessageId)
            .Distinct()
            .ToList();

        using var flushScope = scopeFactory.CreateScope();
        var store = flushScope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var claimed = await store.ClaimByMessageIdsAsync(messageIds, cancellationToken);
        if (claimed.Count == 0)
        {
            return;
        }

        // Build lookup: MessageId → queue of envelopes for correlation
        var envelopesByMessageId = new Dictionary<string, Queue<WrittenOutboxMessage>>();
        foreach (var written in messages)
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
                    using var scope = scopeFactory.CreateScope();
                    var localDispatcher = scope.ServiceProvider.GetService<ILocalEnvelopeDispatcher>();

                    if (localDispatcher is not null && localDispatcher.DeliversLocally(written.Envelope.Type))
                    {
                        await localDispatcher.PublishLocalEnvelopeAsync(written.Envelope, scope.ServiceProvider, cancellationToken);
                    }

                    if (localDispatcher is null || !localDispatcher.IsLocalOnly(written.Envelope.Type))
                    {
                        await transportDispatcher.SendAsync(written.Envelope, cancellationToken);
                    }

                    await store.MarkSentAsync(entry.Id, cancellationToken);
                }
#pragma warning disable CA1031
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    LogFlushSendFailed(logger, entry.Id, written.Envelope.Type, exception);
                    await store.IncrementRetryAsync(entry.Id, cancellationToken);
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
