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
            "Failed to write to dead letter store, falling back to abandon");

    /// <summary>
    /// Creates a new broker transport base.
    /// </summary>
    /// <param name="conduitOptions">The Conduit configuration options.</param>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    protected BrokerTransportBase(
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(conduitOptions);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.ConduitOptions = conduitOptions;
        this.ServiceProvider = serviceProvider;
        this.Logger = logger;
    }

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
    /// The identifier stored on dead letter entries as the source endpoint, and used to resolve
    /// the transport on replay. Defaults to <see cref="ConduitOptions.ApplicationName"/>.
    /// Concrete transports can override this if they need a different identifier.
    /// </summary>
    protected virtual string EndpointName => this.ConduitOptions.ApplicationName ?? string.Empty;

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

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.StopConsumersAsync(cancellationToken);
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

        MessageContext.Observe(new TransportSendStarted(this.TransportName, this.EndpointName));
        var sw = Stopwatch.StartNew();
        try
        {
            var replyData = await this.PublishRequestAsync(envelope, body, headers, cancellationToken);
            sw.Stop();

            ConduitTelemetry.RecordTransportSend(envelope.Type, this.GetType().Name, sw.Elapsed.TotalMilliseconds);
            MessageContext.Observe(new TransportSendCompleted(sw.Elapsed.TotalMilliseconds));

            var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
                replyData, ConduitRuntime.JsonOptions);

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
    public virtual Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            $"{this.GetType().Name} does not support RedeliverAsync. Concrete broker transports must override this method.");
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch — sealed template method
    // -----------------------------------------------------------------------

    /// <summary>
    /// Processes an inbound message: extracts context, creates DI scope, resolves mediator,
    /// dispatches, handles errors, and acknowledges/abandons the message.
    /// Called by the transport's native message handler.
    /// </summary>
    protected async Task ProcessInboundMessageAsync(TMessage message)
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
            // Phase 1: Setup — extract context, create scope, deserialize.
            // If this fails, acknowledge and bail (prevent poison loop).
            DispatchEnvelope envelope;
            IWireMediator wireMediator;
            AsyncServiceScope scope;
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
                if (!this.IsRequest(message)
                    && !string.IsNullOrEmpty(applicationName)
                    && string.Equals(sourceApp, applicationName, StringComparison.Ordinal)
                    && this.IsEventType(messageType))
                {
                    // The generated mediator attaches the host's observer, but a skipped message
                    // never reaches it — attach here so the skip is visible to the audit trail.
                    MessageContext.Current?.AttachObserver(
                        this.ServiceProvider.GetService<IConduitObserver>());
                    MessageContext.Observe(new SelfPublishedEventSkipped(messageType));
                    await this.AcknowledgeAsync(message);

                    // A deliberate skip is a healthy outcome, not a processing error.
                    success = true;
                    return;
                }

                scope = this.ServiceProvider.CreateAsyncScope();
                wireMediator = scope.ServiceProvider.GetRequiredService<IWireMediator>();

                var body = this.GetMessageBody(message);
                var payload = JsonSerializer.Deserialize<JsonElement>(body, ConduitRuntime.JsonOptions);
                envelope = new DispatchEnvelope(messageType, payload);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogMessageProcessingError(this.Logger, messageType, ex);
                await this.AcknowledgeAsync(message);
                return;
            }

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
        try
        {
            var result = await wireMediator.HandleWireRequestAsync(
                envelope, CancellationToken.None);

            if (result.Error is not null)
            {
                handlerException = new InvalidOperationException(
                    $"[{result.Error.Code}] {result.Error.Message}");
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
        // but the message is preserved for inspection/replay.
        if (handlerException is not null)
        {
            await this.TryCaptureErrorAsync(scope, message, messageType, handlerException);
        }

        // Acknowledge exactly once
        await this.AcknowledgeAsync(message);
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
            if (await this.TryCaptureErrorAsync(scope, message, messageType, ex))
            {
                return;
            }

            LogMessageAbandoned(this.Logger, messageType, ex);
            await this.AbandonAsync(message);
        }
        catch (ConduitException)
        {
            // A refusal (the type filter, an unknown type) is final, since a redelivery would be refused again, and
            // HandleWireEvent has logged it: acknowledge, as when the runtime swallowed it.
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

            if (await this.TryCaptureErrorAsync(scope, message, messageType, ex))
            {
                return;
            }

            await this.AbandonAsync(message);
        }
    }

    /// <summary>
    /// Attempts to capture a failed message via a registered <see cref="IInboundErrorInterceptor"/>
    /// (e.g., the dead letter queue). Returns true if captured and acknowledged, false if no
    /// interceptor is registered or capture failed (caller should fall back to abandon).
    /// </summary>
    private async Task<bool> TryCaptureErrorAsync(
        AsyncServiceScope scope,
        TMessage message,
        string messageType,
        Exception exception)
    {
        var interceptor = scope.ServiceProvider.GetService<IInboundErrorInterceptor>();
        if (interceptor is null)
        {
            return false;
        }

        try
        {
            var body = this.GetMessageBody(message);
            var captured = await interceptor.TryCaptureAsync(
                messageType, body, this.EndpointName, exception, CancellationToken.None);
            if (!captured)
            {
                return false;
            }

            await this.AcknowledgeAsync(message);
            LogDeadLetterCaptured(this.Logger, null);
            return true;
        }
#pragma warning disable CA1031
        catch (Exception interceptorException)
#pragma warning restore CA1031
        {
            LogDeadLetterStoreFailed(this.Logger, interceptorException);
            return false;
        }
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

    /// <summary>Stops message receivers/processors and drains pending outbound requests.</summary>
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

    /// <summary>Marks the message for redelivery.</summary>
    protected abstract Task AbandonAsync(TMessage message);

    // -----------------------------------------------------------------------
    // Abstract methods — outbound
    // -----------------------------------------------------------------------

    /// <summary>Whether the transport has been started (connections open).</summary>
    protected abstract bool IsStarted { get; }

    /// <summary>Publishes a request and returns the raw reply payload.</summary>
    /// <param name="envelope">The message being published.</param>
    /// <param name="body">The serialized payload.</param>
    /// <param name="headers">Outbound metadata assembled by the base class. Transports map these
    /// onto their native property model; the dictionary is never shared between publishes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken);

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
