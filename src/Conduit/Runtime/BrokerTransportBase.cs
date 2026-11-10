using System.Diagnostics;
using System.Text.Json;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// Abstract base class for broker transports (Azure Service Bus, RabbitMQ, etc.).
/// Owns the <see cref="IHostedService"/> lifecycle, inbound dispatch orchestration,
/// and outbound send/receive. Transports implement abstract methods for
/// transport-specific primitives (connect, publish, ack/nack, etc.).
/// </summary>
/// <typeparam name="TMessage">The native inbound message type from the broker SDK.</typeparam>
public abstract class BrokerTransportBase<TMessage> : IConduitTransport, IHostedService, IAsyncDisposable
{
    // Shared logging delegates for inbound dispatch
    private static readonly Action<ILogger, string, Exception?> LogReceivedMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(500, "ReceivedMessage"),
            "Received message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogMessageCompleted =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(501, "MessageCompleted"),
            "Completed message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogMessageAbandoned =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(502, "MessageAbandoned"),
            "Abandoned message '{Type}' — broker will redeliver");

    private static readonly Action<ILogger, string, Exception?> LogMessageProcessingError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(503, "MessageProcessingError"),
            "Error processing message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogReplyError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(504, "ReplyError"),
            "Error sending error reply for '{Type}'");

    private static readonly Action<ILogger, Exception?> LogSkippingNoType =
        LoggerMessage.Define(LogLevel.Warning, new EventId(505, "SkippingNoType"),
            "Received message with no type discriminator — acknowledging and skipping");

    private static readonly Action<ILogger, Exception?> LogDeadLetterCaptured =
        LoggerMessage.Define(LogLevel.Information, new EventId(506, "DeadLetterCaptured"),
            "Message captured to dead letter store");

    private static readonly Action<ILogger, Exception?> LogDeadLetterStoreFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(507, "DeadLetterStoreFailed"),
            "Failed to write to dead letter store; abandoning a failed dispatch, acknowledging a message that never reached one");

    private static readonly Action<ILogger, int, Exception?> LogDrainAbandoned =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(508, "DrainAbandoned"),
            "Stopped with {Count} message handler(s) still running at the shutdown deadline; " +
            "the broker redelivers any of their messages not acknowledged before the transport closes");

    private static readonly Action<ILogger, string, Exception?> LogMessageReturnedOnStop =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(509, "MessageReturnedOnStop"),
            "Returned message '{Type}' to the broker undispatched: the transport is stopping");

    private readonly ReplyCorrelator replies = new(TimeProvider.System);

    // The ProcessInboundMessageAsync calls still running, and whether stop has stopped admitting new
    // ones. One lock covers both, so no message is admitted after the drain took its snapshot.
    private readonly Lock inFlightLock = new();
    private readonly HashSet<Task> inFlight = [];
    private bool draining;

    /// <summary>
    /// Creates a new broker transport base.
    /// </summary>
    /// <param name="options">The transport's options (its endpoint name and response timeout among them).</param>
    /// <param name="conduitOptions">The Conduit configuration options.</param>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    protected BrokerTransportBase(
        BrokerTransportOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(conduitOptions);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.Options = options;
        this.ConduitOptions = conduitOptions;
        this.ServiceProvider = serviceProvider;
        this.Logger = logger;
    }

    /// <summary>The transport's options.</summary>
    protected BrokerTransportOptions Options { get; }

    /// <summary>The Conduit configuration options.</summary>
    protected ConduitOptions ConduitOptions { get; }

    /// <summary>The application service provider.</summary>
    protected IServiceProvider ServiceProvider { get; }

    /// <summary>Logger for transport diagnostics.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Short identifier for the transport type (e.g. "rabbitmq", "sqs").
    /// Used in observation records to identify which transport was involved.
    /// </summary>
    protected abstract string TransportName { get; }

    /// <summary>
    /// The endpoint this transport serves (<see cref="BrokerTransportOptions.EndpointName"/>): stored on
    /// dead letter entries as the source endpoint, used to resolve the transport on replay, and named
    /// in a reply timeout.
    /// </summary>
    protected string EndpointName => this.Options.EndpointName;

    // -----------------------------------------------------------------------
    // IHostedService — sealed template methods
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.ConnectAsync(cancellationToken);

        if (this.ConduitOptions.ProducerOnly)
        {
            await this.EnsureOutboundTopologyAsync(cancellationToken);
        }
        else
        {
            var typeRegistry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();

            var eventTypeNames = typeRegistry.ActiveEventTypeNames;
            var commandQueryTypeNames = typeRegistry.ActiveCommandTypeNames
                .Concat(typeRegistry.ActiveQueryTypeNames).ToList();

            await this.EnsureTopologyAsync(eventTypeNames, commandQueryTypeNames, cancellationToken);
            await this.StartConsumersAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stops the transport without abandoning work in progress: it stops receiving, waits for the
    /// handlers already running to finish and acknowledge their messages (until
    /// <paramref name="cancellationToken"/>, the host's shutdown deadline, fires), and only then closes
    /// the channels, links and clients they acknowledge on. A handler still running at the deadline is
    /// left behind: unless it acknowledges before the transport closes, the broker redelivers its message.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown deadline.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Replies keep flowing until the close below: a handler being drained may await one
        await this.StopReceivingAsync(cancellationToken);
        await this.DrainInFlightAsync(cancellationToken);
        await this.StopConsumersAsync(cancellationToken);

        // Nothing reads a reply after this, so a request still waiting fails now rather than at its timeout
        this.FailPendingReplies(new InvalidOperationException(
            $"The transport for endpoint '{this.EndpointName}' stopped before the reply arrived."));
    }

    /// <summary>
    /// Waits for every inbound message still being processed to finish, until
    /// <paramref name="shutdownToken"/> fires; from the moment it is called, a message the broker client
    /// still hands over is returned to the broker undispatched (<see cref="RequeueAsync"/>). Never throws:
    /// at the deadline it logs how many handlers it left running and returns.
    /// </summary>
    /// <param name="shutdownToken">The host's shutdown deadline.</param>
    protected async Task DrainInFlightAsync(CancellationToken shutdownToken)
    {
        Task[] running;
        lock (this.inFlightLock)
        {
            this.draining = true;
            running = [.. this.inFlight];
        }

        if (running.Length == 0)
        {
            return;
        }

        // The tracked tasks are placeholders that only ever complete (a failure still reaches
        // ProcessInboundMessageAsync's caller), so this waits for all of them or the deadline
        var drained = Task.WhenAll(running);
        await drained.WaitAsync(shutdownToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (!drained.IsCompleted)
        {
            LogDrainAbandoned(this.Logger, running.Count(task => !task.IsCompleted), null);
        }
    }

    /// <summary>
    /// Waits for <paramref name="operations"/> until <paramref name="shutdownToken"/> fires, then lets the
    /// rest finish in the background, observing any failure. Never throws. For a broker client whose
    /// stop or close call waits for a running handler however long it takes, whatever token it is given.
    /// </summary>
    /// <param name="operations">The stop or close calls, already started.</param>
    /// <param name="shutdownToken">The host's shutdown deadline.</param>
    protected static async Task WaitUntilDeadlineAsync(IEnumerable<Task> operations, CancellationToken shutdownToken)
    {
        var all = Task.WhenAll(operations);
        await all.WaitAsync(shutdownToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _ = all.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // -----------------------------------------------------------------------
    // IConduitTransport — sealed template methods
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before sending messages.");
        }

        using var activity = ConduitTelemetry.StartProducerActivity<BrokerTransportBase<TMessage>>(envelope.Type);
        activity?.SetTag("conduit.transport", this.GetType().Name);
        var headers = this.BuildOutboundHeaders(activity);

        var body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        // Registered before the publish, so a reply that arrives before the publish call returns
        // still finds its waiter; released when the request ends, however it ends.
        var correlationId = Guid.NewGuid().ToString("N");
        using var pendingReply = this.replies.Register(correlationId, envelope.Type, this.EndpointName);

        MessageContext.Observe(new TransportSendStarted(this.TransportName, this.EndpointName));
        var sw = Stopwatch.StartNew();
        try
        {
            var replyData = await this.PublishRequestAsync(envelope, body, headers, correlationId, cancellationToken);
            sw.Stop();

            ConduitTelemetry.RecordTransportSend(envelope.Type, this.GetType().Name, sw.Elapsed.TotalMilliseconds);
            MessageContext.Observe(new TransportSendCompleted(sw.Elapsed.TotalMilliseconds));

            var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
                replyData.Span, ConduitRuntime.JsonOptions);

            return ConduitRuntime.UnwrapReply(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            MessageContext.Observe(new TransportSendFailed(ex.Message, ex.GetType().Name));
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before publishing events.");
        }

        using var activity = ConduitTelemetry.StartProducerActivity<BrokerTransportBase<TMessage>>(envelope.Type);
        activity?.SetTag("conduit.transport", this.GetType().Name);
        var headers = this.BuildOutboundHeaders(activity);

        var body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        MessageContext.Observe(new TransportSendStarted(this.TransportName, this.EndpointName));
        var sw = Stopwatch.StartNew();
        try
        {
            await this.PublishEventAsync(envelope, body, headers, cancellationToken);
            sw.Stop();

            ConduitTelemetry.RecordTransportSend(envelope.Type, this.GetType().Name, sw.Elapsed.TotalMilliseconds);
            MessageContext.Observe(new TransportSendCompleted(sw.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            sw.Stop();
            MessageContext.Observe(new TransportSendFailed(ex.Message, ex.GetType().Name));
            throw;
        }
    }

    /// <inheritdoc />
    public virtual Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{this.GetType().Name} does not support RedeliverAsync. Concrete broker transports must override this method.");
    }

    /// <summary>
    /// The outbound metadata of a redelivery, keyed as <see cref="ConduitHeaders"/> for the transport's own
    /// header mapping: the correlation ids the message first arrived with (<paramref name="ids"/>), or a fresh
    /// message id when there are none. Nothing else, and above all no source-app stamp: a replayed event is
    /// redelivered on behalf of whoever first published it, and this host's stamp would make its own
    /// self-published filter drop the copy on arrival, silently voiding the replay.
    /// </summary>
    /// <param name="ids">The original correlation ids, or null.</param>
    protected static IReadOnlyDictionary<string, string> BuildRedeliveryHeaders(RedeliveryIds? ids)
    {
        if (ids is null)
        {
            // The receiver defaults the causation and conversation ids to a message id that comes alone
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConduitHeaders.MessageId] = Guid.NewGuid().ToString("N"),
            };
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConduitHeaders.MessageId] = ids.MessageId,
            [ConduitHeaders.CausationId] = ids.CausationId,
            [ConduitHeaders.ConversationId] = ids.ConversationId,
        };
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch — sealed template method
    // -----------------------------------------------------------------------

    /// <summary>
    /// Processes an inbound message: extracts context, creates DI scope, resolves mediator,
    /// dispatches, handles errors, and acknowledges/abandons the message.
    /// Called by the transport's native message handler.
    /// </summary>
    /// <remarks>
    /// Every call is tracked until it finishes, acknowledgement included, so that
    /// <see cref="StopAsync"/> can wait for it before the channel it acknowledges on is closed. Once
    /// stop is draining, a message the broker client still hands over is returned to the broker
    /// undispatched instead.
    /// </remarks>
    protected async Task ProcessInboundMessageAsync(TMessage message)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool admitted;
        lock (this.inFlightLock)
        {
            admitted = !this.draining && this.inFlight.Add(completion.Task);
        }

        if (!admitted)
        {
            await this.ReturnToBrokerAsync(message);
            return;
        }

        try
        {
            await this.DispatchInboundMessageAsync(message);
        }
        finally
        {
            lock (this.inFlightLock)
            {
                this.inFlight.Remove(completion.Task);
            }

            completion.TrySetResult();
        }
    }

    /// <summary>
    /// Hands a message that arrived after stop began draining back to the broker, so another consumer
    /// gets it: <see cref="RequeueAsync"/>, never <see cref="AbandonAsync"/>, since nothing about it failed
    /// and a transport may dead-letter what it abandons. Best effort: the channel or link may already be
    /// closing, and an unsettled message is redelivered either way.
    /// </summary>
    private async Task ReturnToBrokerAsync(TMessage message)
    {
        LogMessageReturnedOnStop(this.Logger, this.GetMessageType(message) ?? "(none)", null);

        try
        {
            await this.RequeueAsync(message);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Unsettled, so the broker redelivers it once the channel or link closes
        }
    }

    private async Task DispatchInboundMessageAsync(TMessage message)
    {
        var messageType = this.GetMessageType(message);
        if (string.IsNullOrEmpty(messageType))
        {
            LogSkippingNoType(this.Logger, null);
            await this.AcknowledgeAsync(message);
            return;
        }

        LogReceivedMessage(this.Logger, messageType, null);

        var parentContext = ConduitTelemetry.ExtractTraceContext(key => this.ExtractMessageHeader(message, key));
        using var activity = ConduitTelemetry.StartConsumerActivity<BrokerTransportBase<TMessage>>(messageType, parentContext);
        activity?.SetTag("conduit.transport", this.GetType().Name);

        var success = false;
        var previousContext = MessageContext.Current;
        try
        {
            // Phase 1: Setup — extract context, create scope, deserialize. A message that fails here
            // never reached a handler, and a redelivery would fail the same way: it is dead-lettered
            // when a store is registered, otherwise acknowledged, so it cannot loop.
            bool selfPublished;
            try
            {
                var (messageId, causationId, conversationId) = this.ExtractContext(message);
                MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId);

                // This application published it, so its listeners already ran inline in
                // PublishAsync. The per-app event queue is bound to every event type, including
                // self-published ones, so the broker routes our own copy straight back to us.
                // Placed after the context starts (Observe no-ops without one) and before the
                // scope is created (a skipped message should cost neither scope nor deserialize).
                var applicationName = this.ConduitOptions.ApplicationName;
                var sourceApp = this.ExtractMessageHeader(message, ConduitHeaders.SourceApp);
                selfPublished = !this.IsRequest(message)
                    && !string.IsNullOrEmpty(applicationName)
                    && string.Equals(sourceApp, applicationName, StringComparison.Ordinal)
                    && this.IsEventType(messageType);

                if (selfPublished)
                {
                    // The generated mediator attaches the host's observer, but a skipped message
                    // never reaches it — attach here so the skip is visible to the audit trail.
                    MessageContext.Current?.AttachObserver(
                        this.ServiceProvider.GetService<IConduitObserver>());
                    MessageContext.Observe(new SelfPublishedEventSkipped(messageType));
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await this.SettleUndispatchableAsync(message, messageType, ex);
                return;
            }

            if (selfPublished)
            {
                await this.AcknowledgeAsync(message);

                // A deliberate skip is a healthy outcome, not a processing error.
                success = true;
                return;
            }

            DispatchEnvelope envelope;
            IWireMediator wireMediator;
            AsyncServiceScope? dispatchScope = null;
            try
            {
                dispatchScope = this.ServiceProvider.CreateAsyncScope();
                wireMediator = dispatchScope.Value.ServiceProvider.GetRequiredService<IWireMediator>();

                var body = this.GetMessageBody(message);
                var payload = JsonSerializer.Deserialize<JsonElement>(body, ConduitRuntime.JsonOptions);
                envelope = new DispatchEnvelope(messageType, payload);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (dispatchScope is { } failedScope)
                {
                    await failedScope.DisposeAsync();
                }

                await this.SettleUndispatchableAsync(message, messageType, ex);
                return;
            }

            var scope = dispatchScope.Value;

            // Phase 2: Dispatch and acknowledge.
            // Each branch owns its own ack/abandon — no shared catch can double-ack.
            await using (scope)
            {
                if (this.IsRequest(message))
                {
                    await this.DispatchRequestAsync(message, messageType, envelope, wireMediator, scope);
                }
                else if (this.IsFireAndForgetCommand(message, messageType, wireMediator))
                {
                    await this.DispatchFireAndForgetCommandAsync(message, messageType, envelope, wireMediator, scope);
                }
                else
                {
                    await this.DispatchEventAsync(message, messageType, envelope, wireMediator, scope);
                }
            }

            success = true;
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
            ConduitTelemetry.RecordTransportReceive(messageType, this.GetType().Name, success);
        }
    }

    private async Task DispatchRequestAsync(
        TMessage message,
        string messageType,
        DispatchEnvelope envelope,
        IWireMediator wireMediator,
        AsyncServiceScope scope)
    {
        // Dispatch and build reply. On failure, build an error reply.
        // HandleWireRequestAsync returns DispatchResult — handler exceptions are converted
        // to Fail results inside ConduitRuntime, so we check the result for failure below.
        BinaryData replyData;
        Exception? handlerException = null;
        string? reference = null;
        try
        {
            var result = await wireMediator.HandleWireRequestAsync(
                envelope, CancellationToken.None);

            // Only an unexpected failure is dead-lettered. A refusal (not found, conflict, a failed
            // precondition, ...) is the handler's answer, already on its way to the caller, and a
            // replay would be refused again. The runtime logged the real exception under the
            // reference the caller is given; the entry records it to find that line.
            if (result.Error is { } error && error.Code == ErrorCode.InternalError.Value)
            {
                handlerException = new InvalidOperationException($"[{error.Code}] {error.Message}");
                reference = error.Reference;
            }

            replyData = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                result, ConduitRuntime.JsonOptions));
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMessageProcessingError(this.Logger, messageType, ex);
            handlerException = ex;

            replyData = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                DispatchResult<object>.Fail("InternalError", ex.Message),
                ConduitRuntime.JsonOptions));
        }

        // Send reply (best-effort)
        try
        {
            await this.SendReplyAsync(message, replyData);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReplyError(this.Logger, messageType, ex);
        }

        // Capture to DLQ if the handler failed — the caller already got an error reply,
        // but the message is preserved for inspection/replay. A capture acknowledges the
        // message itself, and a second settlement is an error to the broker (RabbitMQ closes
        // the channel over the unknown delivery tag), so acknowledge only what it did not take.
        var captured = handlerException is not null
            && await this.TryCaptureErrorAsync(scope.ServiceProvider, message, messageType, handlerException, reference);

        // Acknowledge exactly once
        if (!captured)
        {
            await this.AcknowledgeAsync(message);
        }

        LogMessageCompleted(this.Logger, messageType, null);
    }

    private async Task DispatchEventAsync(
        TMessage message,
        string messageType,
        DispatchEnvelope envelope,
        IWireMediator wireMediator,
        AsyncServiceScope scope)
    {
        // Dispatch, acknowledge on success, abandon on listener failure
        try
        {
            await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);
            await this.AcknowledgeAsync(message);
            LogMessageCompleted(this.Logger, messageType, null);
        }
        catch (EventListenerException ex)
        {
            if (await this.TryCaptureErrorAsync(scope.ServiceProvider, message, messageType, ex, reference: null))
            {
                return;
            }

            LogMessageAbandoned(this.Logger, messageType, ex);
            await this.AbandonAsync(message);
        }
        catch (ConduitException ex) when (ex is BadRequestException or ContractMismatchException)
        {
            // An event this host cannot read (an unknown type, a body its type cannot read) refuses no one: it is a
            // message no redelivery could dispatch. Dead-letter it, else acknowledge it, as a message that failed
            // before dispatch is. HandleWireEvent has logged it.
            if (!await this.TryCaptureErrorAsync(scope.ServiceProvider, message, messageType, ex, reference: null))
            {
                await this.AcknowledgeAsync(message);
            }
        }
        catch (ConduitException)
        {
            // Any other refusal (the type filter's Forbidden, a gate's own) is final, since a redelivery would be
            // refused again, and HandleWireEvent has logged it: acknowledge.
            await this.AcknowledgeAsync(message);
        }
    }

    /// <summary>
    /// Checks whether the message type is a known event type. The self-published filter applies to
    /// events only: a routed <c>[FireAndForget]</c> command is sent via <c>SendEventAsync</c>, so it
    /// also arrives with <c>IsRequest == false</c> and carries this application's own source stamp.
    /// Since <see cref="ConduitOptions.ApplicationName"/> names the application rather than the
    /// instance, an application that routes such a command to an endpoint served by itself — or by a
    /// peer instance of itself — would otherwise have it acknowledged and never handled.
    /// </summary>
    private bool IsEventType(string messageType)
    {
        var typeRegistry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();
        return typeRegistry.EventTypeNames.Contains(messageType);
    }

    /// <summary>
    /// Checks if the inbound message is a fire-and-forget command (not a request, not an event).
    /// Fire-and-forget commands arrive without reply headers but their type is a command type.
    /// </summary>
    private bool IsFireAndForgetCommand(TMessage message, string messageType, IWireMediator _)
    {
        if (this.IsRequest(message))
        {
            return false;
        }

        // Check if this message type is a command/query type in the registry
        var typeRegistry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();
        return typeRegistry.CommandTypeNames.Contains(messageType)
            || typeRegistry.QueryTypeNames.Contains(messageType);
    }

    /// <summary>
    /// Dispatches a fire-and-forget command: runs the handler, acknowledges, no reply sent.
    /// </summary>
    private async Task DispatchFireAndForgetCommandAsync(
        TMessage message,
        string messageType,
        DispatchEnvelope envelope,
        IWireMediator wireMediator,
        AsyncServiceScope scope)
    {
        try
        {
            await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);
            await this.AcknowledgeAsync(message);
            LogMessageCompleted(this.Logger, messageType, null);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMessageProcessingError(this.Logger, messageType, ex);

            if (await this.TryCaptureErrorAsync(scope.ServiceProvider, message, messageType, ex, reference: null))
            {
                return;
            }

            await this.AbandonAsync(message);
        }
    }

    /// <summary>
    /// Settles a message that failed before it could be dispatched (unreadable body, a scope or mediator
    /// that could not be built): captured through a fresh scope when an <see cref="IInboundErrorInterceptor"/>
    /// takes it, otherwise acknowledged, never abandoned — a redelivery would fail the same way, forever.
    /// </summary>
    private async Task SettleUndispatchableAsync(TMessage message, string messageType, Exception exception)
    {
        LogMessageProcessingError(this.Logger, messageType, exception);

        AsyncServiceScope captureScope;
        try
        {
            captureScope = this.ServiceProvider.CreateAsyncScope();
        }
#pragma warning disable CA1031
        catch (Exception scopeException)
#pragma warning restore CA1031
        {
            LogDeadLetterStoreFailed(this.Logger, scopeException);
            await this.AcknowledgeAsync(message);
            return;
        }

        // Only the scope's creation is guarded: a capture's own acknowledgement must not fail into a second one
        await using (captureScope)
        {
            if (await this.TryCaptureErrorAsync(captureScope.ServiceProvider, message, messageType, exception, reference: null))
            {
                return;
            }
        }

        await this.AcknowledgeAsync(message);
    }

    /// <summary>
    /// Attempts to capture a failed message via a registered <see cref="IInboundErrorInterceptor"/>
    /// (e.g., the dead letter queue). Returns true if captured and acknowledged, false if no
    /// interceptor is registered or capture failed (the caller falls back: abandon a failed dispatch,
    /// acknowledge a message that never reached one). The acknowledgement after a capture is outside the
    /// guard: one that throws propagates to the broker client rather than returning false, which would have
    /// the caller settle the message a second time (and the redelivery store it twice).
    /// </summary>
    private async Task<bool> TryCaptureErrorAsync(
        IServiceProvider services,
        TMessage message,
        string messageType,
        Exception exception,
        string? reference)
    {
        try
        {
            var interceptor = services.GetService<IInboundErrorInterceptor>();
            if (interceptor is null)
            {
                return false;
            }

            var body = this.GetMessageBody(message);
            var captured = await interceptor.TryCaptureAsync(
                messageType, body, this.EndpointName, exception, reference, CancellationToken.None);
            if (!captured)
            {
                return false;
            }
        }
#pragma warning disable CA1031
        catch (Exception interceptorException)
#pragma warning restore CA1031
        {
            LogDeadLetterStoreFailed(this.Logger, interceptorException);
            return false;
        }

        await this.AcknowledgeAsync(message);
        LogDeadLetterCaptured(this.Logger, null);
        return true;
    }

    // -----------------------------------------------------------------------
    // Abstract methods — lifecycle
    // -----------------------------------------------------------------------

    /// <summary>Opens broker connections and channels.</summary>
    protected abstract Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Creates exchanges/topics, queues, subscriptions, and bindings.</summary>
    protected abstract Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates only the outbound topology needed for publishing messages and
    /// receiving replies (exchange/topic declaration + reply queue).
    /// Called instead of <see cref="EnsureTopologyAsync"/> when
    /// <see cref="ConduitOptions.ProducerOnly"/> is true.
    /// </summary>
    protected abstract Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken);

    /// <summary>Starts message receivers/processors.</summary>
    protected abstract Task StartConsumersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The first step of <see cref="StopAsync"/>: stop taking new inbound messages from the broker
    /// (cancel consumers, withdraw link credit, stop polling) while leaving open everything a handler
    /// still running acknowledges or replies on, and the reply consumer. Called on a producer-only
    /// host too, where it has nothing to stop. Must not throw for an expired token.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown deadline.</param>
    protected abstract Task StopReceivingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The last step of <see cref="StopAsync"/>, after the in-flight handlers drained or the deadline
    /// passed: close the inbound channels, links or receivers and the reply consumer. A message still
    /// unacknowledged here is redelivered by the broker. Must not throw for an expired token.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown deadline, possibly already expired.</param>
    protected abstract Task StopConsumersAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    // -----------------------------------------------------------------------
    // Abstract methods — inbound dispatch
    // -----------------------------------------------------------------------

    /// <summary>Extracts the type discriminator from native message properties.</summary>
    protected abstract string? GetMessageType(TMessage message);

    /// <summary>Extracts the message body.</summary>
    protected abstract BinaryData GetMessageBody(TMessage message);

    /// <summary>Determines if the message expects a reply (command/query vs event).</summary>
    protected abstract bool IsRequest(TMessage message);

    /// <summary>Extracts MessageContext fields from native message properties/headers.</summary>
    protected abstract (string MessageId, string? CausationId, string ConversationId) ExtractContext(TMessage message);

    /// <summary>Sends a pre-serialized reply to the original sender using native reply mechanism.</summary>
    protected abstract Task SendReplyAsync(TMessage message, BinaryData replyBody);

    /// <summary>Marks the message as successfully processed.</summary>
    protected abstract Task AcknowledgeAsync(TMessage message);

    /// <summary>
    /// Settles a delivery whose dispatch failed and that nothing captured: the broker redelivers it or
    /// dead-letters it, per the transport. Never called for a message that was not dispatched; see
    /// <see cref="RequeueAsync"/>.
    /// </summary>
    protected abstract Task AbandonAsync(TMessage message);

    /// <summary>
    /// Hands a message back to the broker undispatched, for another instance to take: one the broker
    /// client delivered after stop began draining. Nothing about it failed, so a transport whose
    /// <see cref="AbandonAsync"/> dead-letters must override this to requeue. Defaults to
    /// <see cref="AbandonAsync"/>, which redelivers on a broker that does not dead-letter an abandoned
    /// message at once.
    /// </summary>
    protected virtual Task RequeueAsync(TMessage message) => this.AbandonAsync(message);

    // -----------------------------------------------------------------------
    // Abstract methods — outbound
    // -----------------------------------------------------------------------

    /// <summary>Whether the transport has been started (connections open).</summary>
    protected abstract bool IsStarted { get; }

    /// <summary>
    /// Publishes a request and returns the raw reply payload. A transport whose replies arrive on a
    /// shared reply consumer publishes the request carrying <paramref name="correlationId"/>, returns
    /// <see cref="AwaitReplyAsync"/>, and has its consumer call <see cref="CompleteReply"/>.
    /// </summary>
    /// <param name="envelope">The message being published.</param>
    /// <param name="body">The serialized payload.</param>
    /// <param name="headers">Outbound metadata assembled by the base class. Transports map these
    /// onto their native property model; the dictionary is never shared between publishes.</param>
    /// <param name="correlationId">The id the reply must carry back. Its waiter is already registered.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    protected abstract Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the reply to the request published with <paramref name="correlationId"/>, at most
    /// <see cref="BrokerTransportOptions.ResponseTimeout"/>.
    /// </summary>
    /// <exception cref="TimeoutException">No reply arrived within the response timeout. Never an
    /// <see cref="OperationCanceledException"/>, so a caller's catch for its own cancellation cannot
    /// swallow a remote that did not answer.</exception>
    /// <exception cref="OperationCanceledException">Only for <paramref name="cancellationToken"/>, the caller's own.</exception>
    /// <exception cref="InvalidOperationException">The transport stopped before the reply arrived.</exception>
    protected Task<ReadOnlyMemory<byte>> AwaitReplyAsync(string correlationId, CancellationToken cancellationToken) =>
        this.replies.WaitAsync(correlationId, this.Options.ResponseTimeout, cancellationToken);

    /// <summary>
    /// Hands a reply to the request waiting on <paramref name="correlationId"/>. Called by the
    /// transport's reply consumer. The memory must outlive the call: copy a buffer the broker client
    /// reuses. Returns false for a reply nobody awaits (late, after its request timed out, or foreign).
    /// </summary>
    protected bool CompleteReply(string correlationId, ReadOnlyMemory<byte> body) =>
        this.replies.Complete(correlationId, body);

    /// <summary>
    /// Fails every request still waiting for a reply with <paramref name="exception"/>.
    /// <see cref="StopAsync"/> calls it once nothing can read a reply any more.
    /// </summary>
    protected void FailPendingReplies(Exception exception) => this.replies.FailAll(exception);

    /// <summary>
    /// The <see cref="TimeoutException"/> a request of <paramref name="messageType"/> fails with when no
    /// reply arrives within the response timeout, for a transport that waits for replies itself rather
    /// than through <see cref="AwaitReplyAsync"/>.
    /// </summary>
    protected TimeoutException CreateReplyTimeoutException(string messageType) =>
        ReplyCorrelator.NoReply(messageType, this.EndpointName, this.Options.ResponseTimeout);

    /// <summary>Publishes an event (fire-and-forget).</summary>
    /// <param name="envelope">The message being published.</param>
    /// <param name="body">The serialized payload.</param>
    /// <param name="headers">Outbound metadata assembled by the base class.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extracts a single header value from an inbound message.
    /// Called during receive to restore the parent trace context.
    /// Transports map this to their native header format.
    /// </summary>
    protected abstract string? ExtractMessageHeader(TMessage message, string key);

    /// <summary>
    /// Builds the complete outbound metadata set for a single publish: correlation IDs from the
    /// ambient <see cref="MessageContext"/>, W3C trace context from the current activity, and the
    /// publishing application's name.
    /// </summary>
    /// <remarks>
    /// A fresh dictionary per call. This replaced a <c>[ThreadStatic] static Dictionary</c> in each
    /// transport, which was thread-affine rather than call-affine: an async continuation can resume
    /// on a different pool thread than the one that wrote the slot, and a reused pool thread can
    /// carry stale leftovers from an unrelated publish into the next one it services.
    /// </remarks>
    private Dictionary<string, string> BuildOutboundHeaders(Activity? activity)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        var context = MessageContext.Current;
        if (context is not null)
        {
            headers[ConduitHeaders.MessageId] = context.MessageId;
            headers[ConduitHeaders.CausationId] = context.CausationId;
            headers[ConduitHeaders.ConversationId] = context.ConversationId;
        }

        ConduitTelemetry.InjectTraceContext(activity, (key, value) => headers[key] = value);

        var applicationName = this.ConduitOptions.ApplicationName;
        if (!string.IsNullOrEmpty(applicationName))
        {
            headers[ConduitHeaders.SourceApp] = applicationName;
        }

        return headers;
    }
}
