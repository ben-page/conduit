using System.Text.Json;
using Conduit.Configuration;
using Conduit.Exceptions;
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

        var body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        var replyData = await this.PublishRequestAsync(envelope, body, cancellationToken);

        var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
            replyData, ConduitRuntime.JsonOptions);

        return ConduitRuntime.UnwrapReply(result);
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

        var body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        await this.PublishEventAsync(envelope, body, cancellationToken);
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
                    await this.DispatchRequestAsync(message, messageType, envelope, wireMediator);
                }
                else if (this.IsFireAndForgetCommand(message, messageType, wireMediator))
                {
                    await this.DispatchFireAndForgetCommandAsync(message, messageType, envelope, wireMediator);
                }
                else
                {
                    await this.DispatchEventAsync(message, messageType, envelope, wireMediator);
                }
            }
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    private async Task DispatchRequestAsync(
        TMessage message,
        string messageType,
        DispatchEnvelope envelope,
        IWireMediator wireMediator)
    {
        // Dispatch and build reply. On failure, build an error reply.
        BinaryData replyData;
        try
        {
            var result = await wireMediator.HandleWireRequestAsync(
                envelope, CancellationToken.None);

            replyData = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                result, ConduitRuntime.JsonOptions));
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMessageProcessingError(this.Logger, messageType, ex);

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

        // Acknowledge exactly once
        await this.AcknowledgeAsync(message);
        LogMessageCompleted(this.Logger, messageType, null);
    }

    private async Task DispatchEventAsync(
        TMessage message,
        string messageType,
        DispatchEnvelope envelope,
        IWireMediator wireMediator)
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
            LogMessageAbandoned(this.Logger, messageType, ex);
            await this.AbandonAsync(message);
        }
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
        IWireMediator wireMediator)
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
            await this.AbandonAsync(message);
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
    protected abstract Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken);

    /// <summary>Publishes an event (fire-and-forget).</summary>
    protected abstract Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken);
}
